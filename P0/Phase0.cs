using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Unity.Profiling;

namespace Snitch.P0
{
    /// <summary>
    /// Phase-0 verification probes (DEBUG-only; Release-excluded). Each probe is a falsifiable, in-process
    /// check that proves - live in THIS IL2CPP build - whether a risky part of the profiler design works,
    /// before the real engine is built on it. Driven from the console: "snitch p0 &lt;check&gt;". Results are
    /// cached and printed together by "snitch p0 caps".
    ///
    ///   sw          Stopwatch resolution + GetTimestamp() per-call overhead (the section-timer primitive).
    ///   uncap/recap Uncap the framerate so frame-time reflects true cost (and restore it).
    ///   recorders   Do Unity ProfilerRecorder engine counters return non-zero here? (advisory layer.)
    ///   trampoline  Managed Harmony prefix+finalizer per-call overhead (a lower bound for vanilla probes).
    ///   npcpatch    THE key gate: Harmony Prefix+Finalizer on NPCMovement.Update - do calls fire and scale
    ///               with the NPC count, and how much overhead does the patch add? Decides whether per-entity
    ///               vanilla cost-attribution is viable, or NPC cost must come from ablation instead.
    ///   web         In-process WebSocket upgrade self-test (HttpListener + ClientWebSocket) - proves the
    ///               local data server's streaming transport works under this runtime.
    /// </summary>
    internal static class Phase0
    {
        // ---- rolling frame-time ring (the shared measurement substrate) ----
        private const int FR = 120;
        private static readonly double[] _frameRing = new double[FR];
        private static int _frameHead, _frameCount;

        // ---- sw ----
        private static long _swFreq;
        private static string _swResult = "(not run)";

        // ---- uncap ----
        private static int _uncapTimer;
        private static double _uncapBeforeFps;
        private static int _savedVSync = -999, _savedTarget = -999;
        private static string _uncapResult = "(not run)";

        // ---- recorders ----
        private static List<Probe> _probes;
        private static int _recTimer;
        private static string _recResult = "(not run)";

        // ---- trampoline ----
        private static bool _noOpPatched;
        private static volatile int _noOpSink;
        private static string _trampResult = "(not run)";

        // ---- npcpatch ----
        private enum NpcState { Idle, Warm }
        private static NpcState _npcState = NpcState.Idle;
        private static int _npcTimer;
        private static double _npcBaseMs;
        private static bool _npcPatched;
        private static bool _npcAccumulate;
        private static long _npcStartTs;
        private static long _npcTicksFrame;
        private static int _npcCallsFrame;
        private static readonly double[] _npcInsideRing = new double[FR];
        private static readonly int[] _npcCallsRing = new int[FR];
        private static int _npcRingHead, _npcRingCount;
        private static string _npcResult = "(not run)";
        private static bool _npcPass;

        // ---- web ----
        private static HttpListener _p0Listener;
        private static string _webResult = "(not run)";

        // ===================================================================== dispatch

        internal static void Run(string[] p)
        {
            string check = p.Length > 2 ? p[2].ToLowerInvariant() : "caps";
            switch (check)
            {
                case "sw": SwProbe(); break;
                case "uncap": UncapProbe(); break;
                case "recap": Recap(); break;
                case "recorders": RecordersProbe(); break;
                case "trampoline": TrampolineProbe(); break;
                case "npcpatch": NpcPatchProbe(); break;
                case "web": WebProbe(); break;
                case "caps": Caps(); break;
                default: Log($"unknown check '{check}'. Use sw|uncap|recap|recorders|trampoline|npcpatch|web|caps"); break;
            }
        }

        internal static void Tick()
        {
            // shared frame-time ring (warms continuously while in world)
            double ms = Time.unscaledDeltaTime * 1000.0;
            _frameRing[_frameHead] = ms;
            _frameHead = (_frameHead + 1) % FR;
            if (_frameCount < FR) _frameCount++;

            // uncap sampling window
            if (_uncapTimer > 0 && --_uncapTimer == 0)
            {
                double after = FrameMeanFps();
                _uncapResult = $"before~{_uncapBeforeFps:F0} after~{after:F0} fps " +
                               (after > _uncapBeforeFps * 1.1 ? "(PASS - rose)" : "(inconclusive on this scene)") +
                               "; 'snitch p0 recap' restores.";
                Log("uncap: " + _uncapResult);
            }

            // recorders sampling window
            if (_recTimer > 0 && _probes != null)
            {
                for (int i = 0; i < _probes.Count; i++) _probes[i].Sample();
                if (--_recTimer == 0)
                {
                    var sb = new StringBuilder();
                    int measured = 0;
                    for (int i = 0; i < _probes.Count; i++)
                    {
                        Probe p = _probes[i];
                        string st = p.State();
                        if (st == "MEASURED") measured++;
                        sb.Append(p.Label).Append('=').Append(st).Append('(').Append(p.Last).Append(") ");
                    }
                    _recResult = $"{measured}/{_probes.Count} MEASURED :: {sb}";
                    Log("recorders: " + _recResult);
                }
            }

            // npcpatch sampling window
            if (_npcState == NpcState.Warm)
            {
                double insideMs = _npcTicksFrame * 1000.0 / Stopwatch.Frequency;
                _npcInsideRing[_npcRingHead] = insideMs;
                _npcCallsRing[_npcRingHead] = _npcCallsFrame;
                _npcRingHead = (_npcRingHead + 1) % FR;
                if (_npcRingCount < FR) _npcRingCount++;
                _npcTicksFrame = 0;
                _npcCallsFrame = 0;

                if (--_npcTimer <= 0)
                {
                    _npcAccumulate = false;
                    double patchedMs = FrameMeanMs();
                    double insideMean = MeanD(_npcInsideRing, _npcRingCount);
                    double callsMean = MeanI(_npcCallsRing, _npcRingCount);
                    int regCount = NpcRegistryCount();
                    double oNs = callsMean > 0 ? (patchedMs - _npcBaseMs) * 1e6 / callsMean : 0.0;
                    double correctedMs = insideMean - callsMean * (oNs / 1e6);
                    _npcPass = callsMean > 0.5;
                    _npcResult = $"{(_npcPass ? "PASS" : "FAIL")} calls/frame={callsMean:F0} (NPCRegistry={regCount}) " +
                                 $"inside={insideMean:F3}ms/frame base={_npcBaseMs:F3} patched={patchedMs:F3} " +
                                 $"o~={oNs:F0}ns/call corrected={correctedMs:F3}ms/frame";
                    _npcState = NpcState.Idle;
                    Log("npcpatch: " + _npcResult);
                    Log(_npcPass
                        ? "  => per-entity vanilla attribution VIABLE (corrected = inside minus measured patch overhead)."
                        : "  => 0 calls: engine bypasses the managed proxy; NPC cost must use ablation instead.");
                }
            }
        }

        internal static void Shutdown()
        {
            try { if (_savedVSync != -999) { QualitySettings.vSyncCount = _savedVSync; Application.targetFrameRate = _savedTarget; _savedVSync = -999; } } catch { }
            try { if (_probes != null) foreach (Probe p in _probes) p.Dispose(); } catch { }
            try { _p0Listener?.Stop(); _p0Listener?.Close(); } catch { }
            _npcAccumulate = false;
        }

        // ===================================================================== probes

        private static void SwProbe()
        {
            bool hi = Stopwatch.IsHighResolution;
            long freq = Stopwatch.Frequency;
            _swFreq = freq;
            const int N = 1_000_000;
            long acc = 0;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < N; i++) acc += Stopwatch.GetTimestamp();
            long t1 = Stopwatch.GetTimestamp();
            double perNs = (t1 - t0) * 1e9 / freq / N;
            double per12k = perNs * 12000 / 1e6;
            bool pass = hi && per12k < 3.0;
            _swResult = $"{(pass ? "PASS" : "WARN")} IsHighResolution={hi} Frequency={freq} " +
                        $"GetTimestamp~={perNs:F1}ns/call; @12k/s ~= {per12k:F2} ms/s (acc&1={acc & 1})";
            Log("sw: " + _swResult);
        }

        private static void UncapProbe()
        {
            if (_savedVSync == -999) { _savedVSync = QualitySettings.vSyncCount; _savedTarget = Application.targetFrameRate; }
            _uncapBeforeFps = FrameMeanFps();
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            _uncapTimer = FR;
            Log($"uncap: vSync {_savedVSync}->0, targetFR {_savedTarget}->-1. before~{_uncapBeforeFps:F0} fps; sampling {FR} frames...");
        }

        private static void Recap()
        {
            if (_savedVSync != -999)
            {
                QualitySettings.vSyncCount = _savedVSync;
                Application.targetFrameRate = _savedTarget;
                Log($"recap: restored vSync={_savedVSync} targetFR={_savedTarget}");
                _savedVSync = -999; _savedTarget = -999;
            }
            else Log("recap: nothing to restore");
        }

        private static void RecordersProbe()
        {
            if (_probes == null)
            {
                _probes = new List<Probe>
                {
                    new Probe("MainThread", ProfilerCategory.Internal, "Main Thread"),
                    new Probe("GCAlloc/Frame", ProfilerCategory.Memory, "GC Allocated In Frame"),
                    new Probe("SystemUsed", ProfilerCategory.Memory, "System Used Memory"),
                    new Probe("DrawCalls", ProfilerCategory.Render, "Draw Calls Count"),
                    new Probe("SetPass", ProfilerCategory.Render, "SetPass Calls Count"),
                    new Probe("Batches", ProfilerCategory.Render, "Batches Count"),
                    new Probe("Triangles", ProfilerCategory.Render, "Triangles Count"),
                    new Probe("ActiveBodies", ProfilerCategory.Physics, "Active Dynamic Bodies"),
                };
            }
            _recTimer = 90;
            Log($"recorders: sampling {_probes.Count} ProfilerRecorder counters over 90 frames...");
        }

        private static void TrampolineProbe()
        {
            const int N = 200_000;
            _noOpSink = 0;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++) NoOpTarget();
            sw.Stop();
            double baseMs = sw.Elapsed.TotalMilliseconds;

            if (!_noOpPatched)
            {
                try
                {
                    Core.HarmonyInst.Patch(
                        AccessTools.Method(typeof(Phase0), nameof(NoOpTarget)),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(Phase0), nameof(NoOpPre))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(Phase0), nameof(NoOpFin))));
                    _noOpPatched = true;
                }
                catch (Exception e) { _trampResult = "FAIL patch " + e.Message; Log("trampoline: " + _trampResult); return; }
            }

            sw.Restart();
            for (int i = 0; i < N; i++) NoOpTarget();
            sw.Stop();
            double patchedMs = sw.Elapsed.TotalMilliseconds;
            double oNs = (patchedMs - baseMs) * 1e6 / N;
            double per12k = oNs * 12000 / 1e6;
            _trampResult = $"managed prefix+finalizer o~={oNs:F0}ns/call (base {baseMs:F2}ms patched {patchedMs:F2}ms /{N}); " +
                           $"@12k/s ~= {per12k:F2} ms/s (lower bound; IL2CPP detour costs more - see npcpatch). sink={_noOpSink}";
            Log("trampoline: " + _trampResult);
        }

        private static void NpcPatchProbe()
        {
            if (_npcState != NpcState.Idle) { Log("npcpatch: already running."); return; }
            _npcBaseMs = FrameMeanMs();
            if (!_npcPatched)
            {
                try
                {
                    var mi = AccessTools.Method(typeof(NPCMovement), "Update", Type.EmptyTypes);
                    if (mi == null) { _npcResult = "FAIL: NPCMovement.Update not found"; Log("npcpatch: " + _npcResult); return; }
                    Core.HarmonyInst.Patch(mi,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(Phase0), nameof(NpcPrefix))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(Phase0), nameof(NpcFinalizer))));
                    _npcPatched = true;
                    Log("npcpatch: NPCMovement.Update patched OK.");
                }
                catch (Exception e) { _npcResult = "FAIL: patch threw " + e.Message; _npcPass = false; Log("npcpatch: " + _npcResult); return; }
            }
            _npcRingHead = 0; _npcRingCount = 0; _npcTicksFrame = 0; _npcCallsFrame = 0;
            _npcAccumulate = true;
            _npcTimer = 150;
            _npcState = NpcState.Warm;
            Log($"npcpatch: armed, sampling 150 frames (baseline {_npcBaseMs:F3} ms/frame, NPCRegistry={NpcRegistryCount()})...");
        }

        private static void WebProbe()
        {
            int port = Config.Preferences.ServerPort;
            Log($"web: starting in-process WebSocket self-test on 127.0.0.1:{port} ...");
            Task.Run(async () =>
            {
                try { await WebSelfTest(port); }
                catch (Exception e) { _webResult = "FAIL " + e.Message; }
                Log("web: " + _webResult);
            });
        }

        private static async Task WebSelfTest(int port)
        {
            string http = $"http://127.0.0.1:{port}/";
            var l = new HttpListener();
            l.Prefixes.Add(http);
            l.Start();
            _p0Listener = l;

            // server: accept one context, upgrade to WS, push one frame, close.
            Task server = Task.Run(async () =>
            {
                HttpListenerContext ctx = await l.GetContextAsync();
                if (ctx.Request.IsWebSocketRequest)
                {
                    HttpListenerWebSocketContext wsctx = await ctx.AcceptWebSocketAsync(null);
                    WebSocket ws = wsctx.WebSocket;
                    byte[] hello = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"mod\":\"snitch\"}");
                    await ws.SendAsync(new ArraySegment<byte>(hello), WebSocketMessageType.Text, true, CancellationToken.None);
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
                }
                else
                {
                    byte[] b = Encoding.UTF8.GetBytes("snitch p0 web (http ok)");
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                    ctx.Response.Close();
                }
            });

            using (var client = new ClientWebSocket())
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/stream"), cts.Token);
                var buf = new byte[1024];
                WebSocketReceiveResult res = await client.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                string got = Encoding.UTF8.GetString(buf, 0, res.Count);
                _webResult = $"PASS WS upgrade + recv '{got}' (in-process). Browser-from-https-origin (PNA/CORS) verified at deploy.";
                try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token); } catch { }
            }

            await Task.WhenAny(server, Task.Delay(1000));
            try { l.Stop(); l.Close(); } catch { }
            _p0Listener = null;
        }

        private static void Caps()
        {
            Log("===== Snitch Phase-0 Capability Matrix =====");
            Log("sw         : " + _swResult);
            Log("uncap      : " + _uncapResult);
            Log("recorders  : " + _recResult);
            Log("trampoline : " + _trampResult);
            Log("npcpatch   : " + _npcResult);
            Log("web        : " + _webResult);
            Log($"decision   : per-entity NPC attribution {(_npcPass ? "VIABLE" : "-> use ablation")}; " +
                "ProfilerRecorder counters are advisory; frame-time + GC are always load-bearing.");
            Log("============================================");
        }

        // ===================================================================== Harmony targets

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void NoOpTarget() { _noOpSink++; }
        private static void NoOpPre() { }
        private static void NoOpFin() { }

        private static void NpcPrefix() { if (_npcAccumulate) _npcStartTs = Stopwatch.GetTimestamp(); }
        private static void NpcFinalizer()
        {
            if (_npcAccumulate)
            {
                _npcTicksFrame += Stopwatch.GetTimestamp() - _npcStartTs;
                _npcCallsFrame++;
            }
        }

        // ===================================================================== helpers

        private static int NpcRegistryCount()
        {
            try { var reg = NPCManager.NPCRegistry; return reg != null ? reg.Count : -1; }
            catch { return -1; }
        }

        private static double FrameMeanMs() => MeanD(_frameRing, _frameCount);
        private static double FrameMeanFps() { double m = FrameMeanMs(); return m > 0 ? 1000.0 / m : 0.0; }

        private static double MeanD(double[] a, int n)
        {
            if (n <= 0) return 0;
            double s = 0; for (int i = 0; i < n; i++) s += a[i]; return s / n;
        }

        private static double MeanI(int[] a, int n)
        {
            if (n <= 0) return 0;
            long s = 0; for (int i = 0; i < n; i++) s += a[i]; return (double)s / n;
        }

        private static void Log(string m) => Core.Log?.Msg("[snitch.p0] " + m);

        // ----- self-certifying ProfilerRecorder wrapper (minimal port of Litterally's CounterProbe) -----
        private sealed class Probe
        {
            internal string Label { get; }
            internal long Last { get; private set; }
            private ProfilerRecorder _rec;
            private bool _ok;
            private bool _sawNonZero;

            internal Probe(string label, ProfilerCategory cat, string stat)
            {
                Label = label;
                try { _rec = ProfilerRecorder.StartNew(cat, stat); _ok = true; }
                catch { _ok = false; }
            }

            internal void Sample()
            {
                if (!_ok) return;
                try
                {
                    if (_rec.Valid && _rec.Count > 0)
                    {
                        Last = _rec.LastValue;
                        if (Last != 0) _sawNonZero = true;
                    }
                }
                catch { _ok = false; }
            }

            internal string State()
            {
                if (!_ok) return "UNAVAILABLE";
                bool v; try { v = _rec.Valid; } catch { return "UNAVAILABLE"; }
                if (!v) return "UNAVAILABLE";
                return _sawNonZero ? "MEASURED" : "SUSPECT";
            }

            internal void Dispose() { try { if (_ok) _rec.Dispose(); } catch { } }
        }
    }
}
