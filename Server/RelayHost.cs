using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Snitch.Engine;

namespace Snitch.Server
{
    /// <summary>
    /// Hosts a relay session for the game so a phone can reach it across networks (not just the same Wi-Fi). It
    /// opens an outbound WebSocket to relay.doodesch.de as the "host" for a pairing code, then - only while a phone
    /// is connected - streams the latest snapshot (end-to-end encrypted) and applies the phone's control back
    /// against the local game. The token lives only in the QR, so the relay forwards ciphertext only. Reconnects
    /// on drop. Transport is an outbound ClientWebSocket; payloads are AES-GCM (both run under this IL2CPP runtime).
    /// </summary>
    internal static class RelayHost
    {
        private const string RelayBase = "wss://relay.doodesch.de/?app=snitch&role=host&code=";

        private static volatile bool _running;
        private static CancellationTokenSource _cts;
        private static string _code = "";
        private static volatile int _clients;

        internal static bool Running => _running;
        internal static string Code => _code;

        internal static void Start(string code)
        {
            if (_running) return;
            _code = code ?? "";
            _clients = 0;
            _running = true;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => Loop(_cts.Token));
            Core.Log?.Msg("[snitch] relay host on (code " + _code + ") - phone can connect via relay.doodesch.de from any network.");
        }

        internal static void Stop()
        {
            _running = false;
            _clients = 0;
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        private static async Task Loop(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                try { await Session(ct); }
                catch (Exception e) { if (_running) Core.Log?.Warning("[snitch] relay host dropped: " + e.Message); }
                if (_running && !ct.IsCancellationRequested) { try { await Task.Delay(3000, ct); } catch { } }
            }
        }

        private static async Task Session(CancellationToken ct)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(RelayBase + Uri.EscapeDataString(_code)), ct);
            _clients = 0;
            Task recv = ReceiveLoop(ws, ct);
            Task send = SendLoop(ws, ct);
            await Task.WhenAny(recv, send);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }

        // Stream the latest snapshot ~3 Hz while a phone is connected (idle otherwise, so an unused session is free).
        private static async Task SendLoop(ClientWebSocket ws, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                if (_clients > 0)
                {
                    string json = SnitchCore.LatestJson;
                    if (!string.IsNullOrEmpty(json))
                    {
                        string frame = "{\"d\":\"" + RelayCrypto.Encrypt(LanServer.Token, json) + "\"}";
                        byte[] bytes = Encoding.UTF8.GetBytes(frame);
                        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                    }
                }
                await Task.Delay(300, ct);
            }
        }

        private static async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buf = new byte[16384];
            var sb = new StringBuilder();
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                sb.Clear();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                } while (!r.EndOfMessage);
                HandleFrame(sb.ToString());
            }
        }

        private static void HandleFrame(string text)
        {
            if (text.IndexOf("__relay", StringComparison.Ordinal) >= 0)
            {
                if (text.IndexOf("nohost", StringComparison.Ordinal) >= 0) _clients = 0;
                else if (text.IndexOf("join", StringComparison.Ordinal) >= 0 || text.IndexOf("leave", StringComparison.Ordinal) >= 0)
                    _clients = Math.Max(0, ExtractInt(text, "n"));
                return;
            }
            string b64 = ExtractField(text, "d");
            if (string.IsNullOrEmpty(b64)) return;
            try
            {
                string json = RelayCrypto.Decrypt(LanServer.Token, b64);
                if (string.IsNullOrEmpty(json)) return;
                SnitchServer.ApplyControl(ExtractField(json, "cmd"), ExtractField(json, "id"), ExtractField(json, "value"));
            }
            catch (Exception e) { Core.Log?.Warning("[snitch] relay control failed: " + e.Message); }
        }

        // ----- tiny JSON field readers (no dependency) -----

        private static string ExtractField(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return null;
            int i = body.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int c = body.IndexOf(':', i + key.Length + 2); if (c < 0) return null;
            int j = c + 1;
            while (j < body.Length && (body[j] == ' ' || body[j] == '\t')) j++;
            if (j >= body.Length) return null;
            if (body[j] == '"')
            {
                int q2 = body.IndexOf('"', j + 1); if (q2 < 0) return null;
                return body.Substring(j + 1, q2 - j - 1);
            }
            int end = j;
            while (end < body.Length && body[end] != ',' && body[end] != '}' && body[end] != ']') end++;
            return body.Substring(j, end - j).Trim();
        }

        private static int ExtractInt(string body, string key)
        {
            string v = ExtractField(body, key);
            return int.TryParse(v, out int n) ? n : 0;
        }
    }
}
