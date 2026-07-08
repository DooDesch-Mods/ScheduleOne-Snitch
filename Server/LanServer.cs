using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Snitch.Engine;

namespace Snitch.Server
{
    /// <summary>
    /// Optional LAN-facing companion to <see cref="SnitchServer"/> so a phone on the same Wi-Fi can open the
    /// bundled dashboard and drive the profiler (a "scan the QR, use your phone as the remote" flow).
    ///
    /// Why a hand-rolled TcpListener instead of reusing HttpListener: on Windows an HttpListener may only bind
    /// loopback prefixes without admin - binding a LAN IP or the wildcard throws "access denied" unless a
    /// one-time elevated netsh urlacl reservation is made. A raw TcpListener binds 0.0.0.0 with no admin at all,
    /// so this needs no elevation on an end-user machine. The phone loads the page same-origin over plain HTTP, so there is
    /// no mixed-content / Private-Network-Access wall (the reason the hosted HTTPS dashboard can only reach
    /// 127.0.0.1, never a LAN IP). It is a tiny HTTP/1.1 server: no WebSocket - the phone polls /snapshot.
    ///
    /// Security: OFF by default. When on, a per-session pairing token gates every data + control route (only
    /// /health and the static page load without it), because /control can mutate game state (mod panel levers).
    /// The token is carried in the QR the desktop shows and never leaves the LAN.
    /// </summary>
    internal static class LanServer
    {
        private const int MaxRequestBytes = 64 * 1024;   // headers + body ceiling; real requests are tiny
        private const int SocketTimeoutMs = 5000;

        private static TcpListener _listener;
        private static Thread _accept;
        private static volatile bool _running;

        private static int _port;
        private static string _ip = "127.0.0.1";
        private static string _token = "";

        internal static bool Running => _running;
        internal static int Port => _port;
        internal static string Ip => _ip;
        internal static string Token => _token;

        internal static void Start(int port)
        {
            if (_running) return;
            _port = port;
            _ip = DetectLanIp();
            // Per-session pairing token: reuse the configured ServerToken if the user set one, else generate a
            // short random one so the LAN endpoint is never wide open. Shown in the log + the desktop QR.
            _token = string.IsNullOrEmpty(Config.Preferences.ServerToken)
                ? Guid.NewGuid().ToString("N").Substring(0, 8)
                : Config.Preferences.ServerToken;
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _running = true;
                _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "Snitch-LanServer" };
                _accept.Start();
                Core.Log?.Msg($"[snitch] LAN remote on http://{_ip}:{port}/ (token {_token}). Scan the QR in the dashboard from your phone.");
                Core.Log?.Msg($"[snitch] if the phone can't connect, allow Schedule I (or TCP port {port}) through Windows Firewall on your Private network.");
            }
            catch (Exception e)
            {
                _running = false;
                Core.Log?.Error($"[snitch] LAN remote failed to start on {port}: {e.Message} (port in use? change LanPort)");
            }
        }

        internal static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        /// <summary>The "lan" object for /health so the desktop dashboard can build the phone URL + QR. The token is
        /// only ever included on the loopback server (same machine); the LAN server itself omits it so a peer that
        /// can already reach the box can't read the gate off an open /health.</summary>
        internal static string LanInfoJson(bool includeToken)
        {
            if (!_running)
                return "\"lan\":{\"enabled\":false}";
            var sb = new StringBuilder(160);
            sb.Append("\"lan\":{\"enabled\":true,\"ip\":\"").Append(_ip).Append("\",\"port\":").Append(_port)
              .Append(",\"url\":\"http://").Append(_ip).Append(':').Append(_port).Append("/\"");
            if (includeToken) sb.Append(",\"token\":\"").Append(_token).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        // ----- accept + routing -----

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (!_running) return; continue; }
                _ = Task.Run(() => Handle(client));
            }
        }

        private static void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = SocketTimeoutMs;
                    stream.WriteTimeout = SocketTimeoutMs;
                    if (!ReadRequest(stream, out string method, out string path, out string query, out string body)) return;
                    Route(stream, method, path, ParseQuery(query), body);
                }
            }
            catch { /* connection error - drop it */ }
        }

        private static void Route(NetworkStream stream, string method, string path, Dictionary<string, string> q, string body)
        {
            path = path.ToLowerInvariant();

            if (method == "OPTIONS") { WriteStatus(stream, 204, "No Content"); return; }

            if (path == "/health")
            {
                WriteJson(stream, WireProtocol.BuildHealth(SnitchCore.LastFrame, SnitchCore.LastScene, LanInfoJson(false)));
                return;
            }

            // Everything from here is data or control -> require the pairing token.
            if (path == "/snapshot" || path == "/caps" || path == "/control")
            {
                if (!TokenOk(q)) { WriteJson(stream, "{\"ok\":false,\"error\":\"token\"}", 401, "Unauthorized"); return; }
                switch (path)
                {
                    case "/snapshot": WriteJson(stream, SnitchCore.LatestJson ?? EmptySnapshot); return;
                    case "/caps": WriteJson(stream, SnitchCore.CapsJson ?? WireProtocol.BuildCaps()); return;
                    case "/control":
                        string cmd = q.TryGetValue("cmd", out var c) ? c : "";
                        string id = q.TryGetValue("id", out var i) ? i : "";
                        string val = q.TryGetValue("value", out var v) ? v : "";
                        WriteJson(stream, SnitchServer.ApplyControl(cmd, id, val));
                        return;
                }
            }

            if (method != "GET") { WriteStatus(stream, 405, "Method Not Allowed"); return; }

            // Static bundled dashboard. SPA routes (unknown paths with no extension) fall back to index.html.
            if (WebAssets.TryResolve(path, out byte[] bytes, out string contentType))
            {
                WriteBytes(stream, bytes, contentType, path.StartsWith("/assets/"));
                return;
            }
            if (WebAssets.TryResolve("/index.html", out byte[] index, out string indexType))
            {
                WriteBytes(stream, index, indexType, false);
                return;
            }
            WriteStatus(stream, 404, "Not Found");
        }

        private static bool TokenOk(Dictionary<string, string> q)
        {
            // Header token is folded into the query dict under "x-snitch-token" by ReadRequest.
            if (q.TryGetValue("token", out string t) && t == _token) return true;
            if (q.TryGetValue("x-snitch-token", out string h) && h == _token) return true;
            return false;
        }

        // ----- HTTP/1.1 request parsing (hand-rolled, no dependency) -----

        private static bool ReadRequest(NetworkStream stream, out string method, out string path, out string query, out string body)
        {
            method = path = query = body = "";
            var ms = new MemoryStream();
            byte[] buf = new byte[4096];
            int headerEnd = -1;
            while (headerEnd < 0 && ms.Length < MaxRequestBytes)
            {
                int n;
                try { n = stream.Read(buf, 0, buf.Length); } catch { return false; }
                if (n <= 0) break;
                ms.Write(buf, 0, n);
                headerEnd = IndexOfDoubleCrlf(ms.GetBuffer(), (int)ms.Length);
            }
            if (headerEnd < 0) return false;

            string header = Encoding.ASCII.GetString(ms.GetBuffer(), 0, headerEnd);
            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return false;

            string[] req = lines[0].Split(' ');
            if (req.Length < 2) return false;
            method = req[0].ToUpperInvariant();
            string target = req[1];
            int qi = target.IndexOf('?');
            if (qi >= 0) { path = target.Substring(0, qi); query = target.Substring(qi + 1); }
            else path = target;

            // Fold an x-snitch-token header into the query string so TokenOk sees one source.
            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string key = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                string value = lines[i].Substring(colon + 1).Trim();
                if (key == "content-length") int.TryParse(value, out contentLength);
                else if (key == "x-snitch-token") query = (query.Length > 0 ? query + "&" : "") + "x-snitch-token=" + Uri.EscapeDataString(value);
            }

            if (contentLength > 0)
            {
                int bodyStart = headerEnd + 4;
                int have = (int)ms.Length - bodyStart;
                while (have < contentLength && ms.Length < MaxRequestBytes)
                {
                    int n;
                    try { n = stream.Read(buf, 0, buf.Length); } catch { break; }
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                    have += n;
                }
                int take = Math.Min(contentLength, (int)ms.Length - bodyStart);
                if (take > 0) body = Encoding.UTF8.GetString(ms.GetBuffer(), bodyStart, take);
            }
            return true;
        }

        private static int IndexOfDoubleCrlf(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
            return -1;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return d;
            foreach (string pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string k = eq < 0 ? pair : pair.Substring(0, eq);
                string v = eq < 0 ? "" : pair.Substring(eq + 1);
                try { k = Uri.UnescapeDataString(k); v = Uri.UnescapeDataString(v); } catch { }
                d[k] = v;
            }
            return d;
        }

        // ----- response helpers -----

        private static void WriteJson(NetworkStream stream, string json, int code = 200, string reason = "OK")
            => WriteResponse(stream, code, reason, "application/json", Encoding.UTF8.GetBytes(json ?? ""), false);

        private static void WriteBytes(NetworkStream stream, byte[] bytes, string contentType, bool cache)
            => WriteResponse(stream, 200, "OK", contentType, bytes, cache);

        private static void WriteStatus(NetworkStream stream, int code, string reason)
            => WriteResponse(stream, code, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(reason), false);

        private static void WriteResponse(NetworkStream stream, int code, string reason, string contentType, byte[] body, bool cache)
        {
            try
            {
                var head = new StringBuilder(256);
                head.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
                head.Append("Content-Type: ").Append(contentType).Append("\r\n");
                head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                head.Append(cache ? "Cache-Control: public, max-age=2592000, immutable\r\n" : "Cache-Control: no-store\r\n");
                head.Append("Connection: close\r\n\r\n");
                byte[] headerBytes = Encoding.ASCII.GetBytes(head.ToString());
                stream.Write(headerBytes, 0, headerBytes.Length);
                if (body.Length > 0) stream.Write(body, 0, body.Length);
                stream.Flush();
            }
            catch { }
        }

        private const string EmptySnapshot =
            "{\"type\":\"snapshot\",\"v\":1,\"frame\":{},\"sections\":[],\"counters\":[],\"states\":[],\"panels\":[],\"logs\":{\"timeline\":[]}}";

        // ----- LAN IP detection -----

        /// <summary>The machine's LAN IPv4 as seen on the default route. A UDP socket "connected" to a public
        /// address (no packets sent) exposes the outbound interface's local IP - this reliably picks the real
        /// Wi-Fi/Ethernet adapter over WSL/VPN/virtual adapters and APIPA. Falls back to DNS, then loopback.</summary>
        private static string DetectLanIp()
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect("8.8.8.8", 65530);
                if (s.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
                    return ep.Address.ToString();
            }
            catch { }
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (a.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a)) continue;
                    string s = a.ToString();
                    if (!s.StartsWith("169.254")) return s;
                }
            }
            catch { }
            return "127.0.0.1";
        }
    }
}
