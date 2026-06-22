using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Snitch.Engine;

namespace Snitch.Server
{
    /// <summary>
    /// The local data server: a loopback HTTP + WebSocket endpoint (127.0.0.1) that streams live profiler data
    /// to the SnitchWeb dashboard (hosted or the bundled offline copy) and serves the offline viewer. Same
    /// proven shape as ScheduleMCP's bridge - HttpListener + background accept thread, work marshalled to the
    /// main thread via a queue, System.Text-free hand-rolled JSON. The in-process WebSocket upgrade works
    /// under this IL2CPP runtime.
    ///
    /// Security: binds 127.0.0.1 only; validates the browser Origin against an allowlist (localhost always OK);
    /// answers Chrome's Private Network Access preflight (Access-Control-Allow-Private-Network); optional token.
    /// Routes: GET /health, GET /snapshot, GET /caps, POST /control, WS /stream, GET / (static offline viewer).
    /// </summary>
    internal static class SnitchServer
    {
        private const int MaxClients = 8;          // cap concurrent dashboards so connections can't accumulate
        private const int SendTimeoutMs = 10000;   // a stuck send (half-open client) aborts instead of leaking
        private static HttpListener _listener;
        private static Thread _accept;
        private static volatile bool _running;
        private static CancellationTokenSource _cts;

        private static int _port;
        private static string _token = "";
        private static string[] _origins = Array.Empty<string>();
        private static string _wwwroot;

        private static readonly List<Session> _sockets = new List<Session>();
        private static readonly object _lock = new object();
        private static readonly ConcurrentQueue<Action> _mainQueue = new ConcurrentQueue<Action>();

        /// <summary>One connected dashboard. Its Gate serializes sends so we never have two SendAsync in flight
        /// on the same WebSocket (which .NET forbids) - the root cause the review flagged.</summary>
        private sealed class Session
        {
            internal readonly WebSocket Ws;
            internal readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            internal Session(WebSocket ws) { Ws = ws; }
        }

        internal static bool Running => _running;
        internal static int SocketCount { get { lock (_lock) return _sockets.Count; } }

        internal static void Start(int port, string token, string allowedOrigins)
        {
            if (_running) return;
            _port = port;
            _token = token ?? "";
            _origins = ParseOrigins(allowedOrigins);
            _wwwroot = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "Snitch", "wwwroot");
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _running = true;
                _cts = new CancellationTokenSource();
                _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "Snitch-Server" };
                _accept.Start();
                Core.Log?.Msg($"[snitch] data server on http://127.0.0.1:{port}/ (ws://127.0.0.1:{port}/stream). token {(_token.Length > 0 ? "on" : "off")}.");
            }
            catch (Exception e)
            {
                _running = false;
                Core.Log?.Error($"[snitch] data server failed to start on {port}: {e.Message} (port in use? change ServerPort)");
            }
        }

        internal static void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }   // unblock all awaiting receive loops + sends
            lock (_lock) { foreach (Session s in _sockets) { try { s.Ws.Abort(); } catch { } try { s.Gate.Dispose(); } catch { } } _sockets.Clear(); }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _listener = null;
        }

        /// <summary>Drain control actions onto the main thread (called from Core.OnUpdate).</summary>
        internal static void Pump()
        {
            int n = 0;
            while (n++ < 8 && _mainQueue.TryDequeue(out Action a))
            {
                try { a(); } catch (Exception e) { Core.Log?.Warning("[snitch] control failed: " + e.Message); }
            }
        }

        /// <summary>Push a snapshot to every connected dashboard (called on the main thread from SnitchCore.Poll).
        /// Each socket's send is serialized by its Gate; if a previous send is still in flight (slow/backpressured
        /// client) this frame is dropped for that client rather than overlapping a SendAsync. Never blocks the
        /// main thread.</summary>
        internal static void Broadcast(string json)
        {
            if (!_running || string.IsNullOrEmpty(json)) return;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            Session[] sessions;
            lock (_lock) { sessions = _sockets.ToArray(); }
            for (int i = 0; i < sessions.Length; i++) SendFireAndForget(sessions[i], bytes);
        }

        private static void SendFireAndForget(Session s, byte[] bytes)
        {
            if (s.Ws.State != WebSocketState.Open) { Remove(s); return; }
            if (!s.Gate.Wait(0)) return;   // a send is already in flight -> coalesce (drop this frame for this client)
            _ = SendAndRelease(s, bytes);
        }

        private static async Task SendAndRelease(Session s, byte[] bytes)
        {
            try { await SendWithTimeout(s.Ws, bytes, _cts?.Token ?? CancellationToken.None).ConfigureAwait(false); }
            catch { Remove(s); try { s.Ws.Abort(); } catch { } }
            finally { try { s.Gate.Release(); } catch { } }
        }

        private static void Remove(Session s) { lock (_lock) { _sockets.Remove(s); } }

        // ----- accept + routing -----

        private static void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { if (!_running) return; continue; }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private static async Task HandleAsync(HttpListenerContext ctx)
        {
          try
          {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            string origin = req.Headers["Origin"];
            ApplyCors(res, origin);

            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }   // CORS / PNA preflight

            string path = req.Url.AbsolutePath.ToLowerInvariant();

            if (req.IsWebSocketRequest)
            {
                if (!OriginAllowed(origin) || !TokenOk(req)) { res.StatusCode = 403; res.Close(); return; }
                await HandleWsAsync(ctx).ConfigureAwait(false);
                return;
            }

            switch (path)
            {
                case "/health":
                    WriteJson(res, WireProtocol.BuildHealth(SnitchCore.LastFrame, SnitchCore.LastScene));
                    break;
                case "/snapshot":
                    if (!TokenOk(req)) { res.StatusCode = 401; res.Close(); break; }
                    WriteJson(res, SnitchCore.LatestJson ?? "{\"type\":\"snapshot\",\"v\":1,\"frame\":{},\"sections\":[],\"counters\":[],\"states\":[]}");
                    break;
                case "/caps":
                    if (!TokenOk(req)) { res.StatusCode = 401; res.Close(); break; }
                    WriteJson(res, SnitchCore.CapsJson ?? WireProtocol.BuildCaps());
                    break;
                case "/control":
                    if (!TokenOk(req)) { res.StatusCode = 401; res.Close(); break; }
                    HandleControl(req, res);
                    break;
                default:
                    ServeStatic(path, res);
                    break;
            }
          }
          catch (Exception e) { Core.Log?.Warning("[snitch] request error: " + e.Message); try { ctx.Response.Abort(); } catch { } }
        }

        private static void HandleControl(HttpListenerRequest req, HttpListenerResponse res)
        {
            string cmd = req.QueryString["cmd"];
            if (string.IsNullOrEmpty(cmd))
            {
                try { using var sr = new StreamReader(req.InputStream, Encoding.UTF8); string body = sr.ReadToEnd(); cmd = ExtractCmd(body); } catch { }
            }
            cmd = (cmd ?? "").Trim().ToLowerInvariant();
            switch (cmd)
            {
                case "start": _mainQueue.Enqueue(SnitchCore.Start); break;
                case "stop": _mainQueue.Enqueue(SnitchCore.Stop); break;
                case "reset": _mainQueue.Enqueue(() => { SnitchCore.Stop(); SnitchCore.Start(); }); break;
                default: WriteJson(res, "{\"ok\":false,\"error\":\"unknown cmd\"}"); return;
            }
            WriteJson(res, "{\"ok\":true,\"cmd\":\"" + cmd + "\"}");
        }

        private static async Task HandleWsAsync(HttpListenerContext ctx)
        {
            lock (_lock) { if (_sockets.Count >= MaxClients) { try { ctx.Response.StatusCode = 503; ctx.Response.Close(); } catch { } return; } }

            WebSocket ws;
            try { ws = (await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket; }
            catch (Exception e) { Core.Log?.Warning("[snitch] ws upgrade failed: " + e.Message); return; }

            CancellationToken ct = _cts?.Token ?? CancellationToken.None;
            var session = new Session(ws);

            // Send the current snapshot immediately. The socket is NOT yet in _sockets, so Broadcast can't see
            // it and no gate is needed; bound the send with a timeout so a dead client can't hang it.
            try { string j = SnitchCore.LatestJson; if (!string.IsNullOrEmpty(j)) await SendWithTimeout(ws, Encoding.UTF8.GetBytes(j), ct).ConfigureAwait(false); }
            catch { }
            lock (_lock) _sockets.Add(session);   // now visible to Broadcast

            // Receive loop. AWAITING ReceiveAsync frees the ThreadPool thread between messages (the dashboard
            // rarely sends), so long-lived dashboards no longer each pin a thread forever - the fix for the
            // long-session "profiler hangs" report. The server CancellationToken lets Stop() unblock them all.
            var buf = new byte[4096];
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { /* client gone or cancelled */ }
            finally
            {
                Remove(session);
                try { ws.Abort(); } catch { }
                try { session.Gate.Dispose(); } catch { }
            }
        }

        /// <summary>Send a frame, aborting if it can't complete within the timeout (a half-open client must not
        /// hold the socket's send gate forever).</summary>
        private static async Task SendWithTimeout(WebSocket ws, byte[] bytes, CancellationToken serverCt)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            cts.CancelAfter(SendTimeoutMs);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
        }

        private static void ServeStatic(string path, HttpListenerResponse res)
        {
            if (path == "/" || string.IsNullOrEmpty(path)) path = "/index.html";
            string rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string full = _wwwroot != null ? Path.GetFullPath(Path.Combine(_wwwroot, rel)) : null;

            // path-traversal guard + existence
            if (full == null || !full.StartsWith(_wwwroot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                if (path == "/index.html") { WriteHtml(res, PlaceholderHtml()); return; }
                res.StatusCode = 404; res.Close(); return;
            }
            try
            {
                byte[] bytes = File.ReadAllBytes(full);
                res.StatusCode = 200;
                res.ContentType = ContentType(full);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Close();
                res.Close();
            }
            catch { try { res.StatusCode = 500; res.Close(); } catch { } }
        }

        // ----- CORS / origin / token -----

        private static void ApplyCors(HttpListenerResponse res, string origin)
        {
            try
            {
                res.Headers["Access-Control-Allow-Origin"] = OriginAllowed(origin) ? (string.IsNullOrEmpty(origin) ? "*" : origin) : "null";
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "content-type, x-snitch-token";
                res.Headers["Access-Control-Allow-Private-Network"] = "true";   // Chrome PNA: public https page -> local server
                res.Headers["Vary"] = "Origin";
            }
            catch { }
        }

        private static bool OriginAllowed(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;   // non-browser / same-origin / direct
            if (origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                origin.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase)) return true;
            for (int i = 0; i < _origins.Length; i++)
                if (string.Equals(_origins[i], origin, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool TokenOk(HttpListenerRequest req)
        {
            if (_token.Length == 0) return true;
            string t = req.Headers["x-snitch-token"];
            if (string.IsNullOrEmpty(t)) t = req.QueryString["token"];
            return t == _token;
        }

        private static string[] ParseOrigins(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
            string[] parts = csv.Split(',');
            var list = new List<string>(parts.Length);
            foreach (string p in parts) { string s = p.Trim().TrimEnd('/'); if (s.Length > 0) list.Add(s); }
            return list.ToArray();
        }

        // ----- response helpers -----

        private static void WriteJson(HttpListenerResponse res, string json) => Write(res, "application/json", Encoding.UTF8.GetBytes(json));
        private static void WriteHtml(HttpListenerResponse res, string html) => Write(res, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));

        private static void Write(HttpListenerResponse res, string type, byte[] bytes)
        {
            try
            {
                res.StatusCode = 200;
                res.ContentType = type;
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.OutputStream.Close();
            }
            catch { }
            finally { try { res.Close(); } catch { } }
        }

        private static string ContentType(string path)
        {
            string e = Path.GetExtension(path).ToLowerInvariant();
            switch (e)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript";
                case ".css": return "text/css";
                case ".json": return "application/json";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }

        private static string ExtractCmd(string body)
        {
            // tiny extractor for {"cmd":"start"} without a JSON dependency
            int i = body.IndexOf("\"cmd\"", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int c = body.IndexOf(':', i); if (c < 0) return null;
            int q1 = body.IndexOf('"', c + 1); if (q1 < 0) return null;
            int q2 = body.IndexOf('"', q1 + 1); if (q2 < 0) return null;
            return body.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static string PlaceholderHtml()
        {
            return "<!doctype html><html><head><meta charset=utf-8><title>Snitch</title>" +
                   "<style>body{font:14px system-ui;background:#0b0e14;color:#cdd6f4;padding:40px;max-width:640px;margin:auto}" +
                   "code{background:#1e2230;padding:2px 6px;border-radius:4px}a{color:#89b4fa}</style></head><body>" +
                   "<h1>Snitch data server</h1><p>This loopback endpoint is live. The offline dashboard isn't bundled in this build yet.</p>" +
                   "<p>Open the hosted dashboard and it will auto-connect to <code>ws://127.0.0.1:" + _port + "/stream</code>, " +
                   "or fetch <code>/snapshot</code> / <code>/health</code> / <code>/caps</code> directly.</p>" +
                   "<p>Support: <a href=\"https://support.doodesch.de\">support.doodesch.de</a></p></body></html>";
        }
    }
}
