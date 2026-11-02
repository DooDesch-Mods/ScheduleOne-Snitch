using System;
using System.IO;
using System.IO.Compression;
using Snitch.Server;

namespace Snitch.UI
{
    /// <summary>
    /// Builds the "connect a phone" QR for the overlay as PNG bytes, the form a Sideload surface takes pictures in.
    /// With a relay session the code points at the hosted site carrying the pairing code, which works from any
    /// network; otherwise it is the LAN-direct URL, which needs the dashboard bundled beside the DLL. Returns null
    /// while the LAN endpoint is off or neither form is available, so the panel shows no picture until it can.
    /// The result is cached by URL, so polling it every frame costs nothing once built.
    /// </summary>
    internal static class QrImage
    {
        private const int Scale = 6;   // pixels per module, large enough for a phone camera to read off the screen
        private const int Quiet = 4;   // mandatory light border, in modules

        private static byte[] _cache;
        private static string _cacheUrl;

        /// <summary>The URL the current image encodes; empty when there is none.</summary>
        internal static string Url => _cacheUrl ?? "";

        internal static byte[] Build()
        {
            if (!LanServer.Running) { _cache = null; _cacheUrl = null; return null; }

            string lan = WebAssets.HasBundledDashboard() ? "&lan=" + LanServer.Ip + ":" + LanServer.Port : "";
            string url;
            if (RelayHost.Running && !string.IsNullOrEmpty(RelayHost.Code))
                url = "https://snitch.doodesch.de/#join=" + RelayHost.Code + "&t=" + LanServer.Token + lan;
            else if (lan.Length > 0)
                url = "http://" + LanServer.Ip + ":" + LanServer.Port + "/#remote&t=" + LanServer.Token;
            else { _cache = null; _cacheUrl = null; return null; }
            if (url == _cacheUrl && _cache != null) return _cache;

            bool[,] m = QrCode.Encode(url);
            if (m == null)
            {
                Core.Log?.Warning("[snitch] the phone QR could not be encoded for " + url.Length + " characters.");
                return null;
            }

            _cache = EncodePng(m);
            _cacheUrl = url;
            return _cache;
        }

        /// <summary>An 8-bit greyscale PNG: black modules on white, with the quiet zone.</summary>
        private static byte[] EncodePng(bool[,] m)
        {
            int n = m.GetLength(0);
            int dim = (n + Quiet * 2) * Scale;
            var raw = new byte[(dim + 1) * dim];
            for (int y = 0; y < dim; y++)
            {
                int rowStart = y * (dim + 1);
                raw[rowStart] = 0;   // filter type: none
                int r = y / Scale - Quiet;
                for (int x = 0; x < dim; x++)
                {
                    int c = x / Scale - Quiet;
                    bool dark = r >= 0 && c >= 0 && r < n && c < n && m[r, c];
                    raw[rowStart + 1 + x] = dark ? (byte)0 : (byte)255;
                }
            }

            byte[] idat;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
                idat = ms.ToArray();
            }

            using (var o = new MemoryStream())
            {
                o.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
                var ihdr = new byte[13];
                WriteBe(ihdr, 0, dim);
                WriteBe(ihdr, 4, dim);
                ihdr[8] = 8;    // bit depth
                ihdr[9] = 0;    // greyscale
                Chunk(o, "IHDR", ihdr);
                Chunk(o, "IDAT", idat);
                Chunk(o, "IEND", Array.Empty<byte>());
                return o.ToArray();
            }
        }

        private static void WriteBe(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
        }

        private static void Chunk(Stream o, string type, byte[] data)
        {
            var head = new byte[8];
            WriteBe(head, 0, data.Length);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            o.Write(head, 0, 8);
            o.Write(data, 0, data.Length);

            uint crc = 0xFFFFFFFF;
            crc = Crc(crc, head, 4, 4);
            crc = Crc(crc, data, 0, data.Length);
            var tail = new byte[4];
            WriteBe(tail, 0, unchecked((int)(crc ^ 0xFFFFFFFF)));
            o.Write(tail, 0, 4);
        }

        private static uint[] _table;

        private static uint Crc(uint crc, byte[] b, int off, int len)
        {
            if (_table == null)
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[i] = c;
                }
                _table = t;
            }
            for (int i = 0; i < len; i++) crc = _table[(crc ^ b[off + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
