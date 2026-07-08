using System;
using System.Security.Cryptography;
using System.Text;

namespace Snitch.Server
{
    /// <summary>
    /// End-to-end encryption for the relay path, wire-compatible with the browser (SnitchWeb/src/relay.ts): AES-GCM
    /// with key = SHA-256("snitch-relay:v1:" + token), and each frame is base64(iv[12] || ciphertext || tag[16]).
    /// The token only ever travels in the QR, so the relay forwards ciphertext only.
    /// </summary>
    internal static class RelayCrypto
    {
        private static byte[] DeriveKey(string token)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes("snitch-relay:v1:" + (token ?? "")));
        }

        internal static string Encrypt(string token, string json)
        {
            byte[] key = DeriveKey(token);
            byte[] iv = new byte[12];
            RandomNumberGenerator.Fill(iv);
            byte[] pt = Encoding.UTF8.GetBytes(json);
            byte[] ct = new byte[pt.Length];
            byte[] tag = new byte[16];
            using (var gcm = new AesGcm(key)) gcm.Encrypt(iv, pt, ct, tag);

            byte[] wire = new byte[12 + ct.Length + 16];
            Buffer.BlockCopy(iv, 0, wire, 0, 12);
            Buffer.BlockCopy(ct, 0, wire, 12, ct.Length);
            Buffer.BlockCopy(tag, 0, wire, 12 + ct.Length, 16);
            return Convert.ToBase64String(wire);
        }

        internal static string Decrypt(string token, string b64)
        {
            byte[] wire = Convert.FromBase64String(b64);
            if (wire.Length < 12 + 16) return null;
            byte[] key = DeriveKey(token);
            byte[] iv = new byte[12];
            byte[] tag = new byte[16];
            int ctLen = wire.Length - 12 - 16;
            byte[] ct = new byte[ctLen];
            Buffer.BlockCopy(wire, 0, iv, 0, 12);
            Buffer.BlockCopy(wire, 12, ct, 0, ctLen);
            Buffer.BlockCopy(wire, 12 + ctLen, tag, 0, 16);
            byte[] pt = new byte[ctLen];
            using (var gcm = new AesGcm(key)) gcm.Decrypt(iv, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
    }
}
