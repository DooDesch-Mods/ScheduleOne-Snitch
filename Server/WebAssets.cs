using System;
using System.IO;

namespace Snitch.Server
{
    /// <summary>
    /// Serves the bundled offline dashboard (the built SnitchWeb SPA copied to Mods/Snitch/wwwroot). Shared by
    /// both the loopback <see cref="SnitchServer"/> and the LAN-facing <see cref="LanServer"/> so the static
    /// serving + content-type + path-traversal guard live in exactly one place.
    /// </summary>
    internal static class WebAssets
    {
        private static string _wwwroot;

        /// <summary>Absolute path to the bundled dashboard root (Mods/Snitch/wwwroot). Computed once.</summary>
        internal static string Wwwroot => _wwwroot ??= Path.Combine(Directory.GetCurrentDirectory(), "Mods", "Snitch", "wwwroot");

        /// <summary>True when a real bundled dashboard is present (index.html exists). The DLL-only release ships no
        /// dashboard, so the LAN-direct shortcut is only offered when this is true; otherwise a phone uses the relay.</summary>
        internal static bool HasBundledDashboard()
        {
            try { return File.Exists(Path.Combine(Wwwroot, "index.html")); } catch { return false; }
        }

        /// <summary>Resolve a request path to a bundled file. Maps "/" to "/index.html", guards against path
        /// traversal, and returns false (with a caller-decided fallback) when the file is missing.</summary>
        internal static bool TryResolve(string path, out byte[] bytes, out string contentType)
        {
            bytes = null; contentType = null;
            if (string.IsNullOrEmpty(path) || path == "/") path = "/index.html";

            string root = Wwwroot;
            string rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, rel)); }
            catch { return false; }

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return false;
            try { bytes = File.ReadAllBytes(full); contentType = ContentType(full); return true; }
            catch { return false; }
        }

        internal static string ContentType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript";
                case ".css": return "text/css";
                case ".json": return "application/json";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".woff2": return "font/woff2";
                default: return "application/octet-stream";
            }
        }
    }
}
