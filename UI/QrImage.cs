using Snitch.Server;

namespace Snitch.UI
{
    /// <summary>
    /// Builds the "connect a phone" QR for the in-game Snitch panel as raw ARGB pixels (the shape the Hotline
    /// image API expects: {width, height, argb...}). It encodes the LAN-direct URL - a phone on the same Wi-Fi
    /// scans it and the game serves the dashboard straight to the phone, no PC browser and no cloud. Returns null
    /// when the LAN endpoint is off, so the panel simply shows no image until it is enabled. Cached by URL so the
    /// per-frame poll is free once built.
    /// </summary>
    internal static class QrImage
    {
        private const int Scale = 6;   // pixels per module - large enough for a phone camera to read off the screen
        private const int Quiet = 4;   // mandatory light border, in modules

        private static int[] _cache;
        private static string _cacheUrl;

        internal static int[] Build()
        {
            if (!LanServer.Running) { _cache = null; _cacheUrl = null; return null; }

            // With the game hosting a relay session, point the QR at the hosted site carrying the relay pairing code
            // (works from ANY network) plus the LAN shortcut (a same-Wi-Fi phone can connect directly). Without the
            // relay, fall back to the LAN-only URL (same Wi-Fi required).
            string url = RelayHost.Running && !string.IsNullOrEmpty(RelayHost.Code)
                ? "https://snitch.doodesch.de/#join=" + RelayHost.Code + "&t=" + LanServer.Token + "&lan=" + LanServer.Ip + ":" + LanServer.Port
                : "http://" + LanServer.Ip + ":" + LanServer.Port + "/#remote&t=" + LanServer.Token;
            if (url == _cacheUrl && _cache != null) return _cache;

            bool[,] m = QrCode.Encode(url);
            if (m == null) return null;

            _cache = Rasterize(m);
            _cacheUrl = url;
            return _cache;
        }

        private static int[] Rasterize(bool[,] m)
        {
            int n = m.GetLength(0);
            int dim = (n + Quiet * 2) * Scale;
            var px = new int[2 + dim * dim];
            px[0] = dim;
            px[1] = dim;

            const int white = unchecked((int)0xFFFFFFFF);
            const int black = unchecked((int)0xFF000000);
            for (int i = 0; i < dim * dim; i++) px[2 + i] = white;

            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    if (!m[r, c]) continue;
                    int x0 = (c + Quiet) * Scale;
                    int y0 = (r + Quiet) * Scale;
                    for (int dy = 0; dy < Scale; dy++)
                    {
                        int rowBase = 2 + (y0 + dy) * dim + x0;
                        for (int dx = 0; dx < Scale; dx++) px[rowBase + dx] = black;
                    }
                }
            return px;
        }
    }
}
