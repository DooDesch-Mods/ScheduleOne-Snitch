using System;
using System.Collections.Generic;

namespace Snitch.UI
{
    /// <summary>
    /// A tiny, dependency-free QR Code encoder (byte mode, error-correction level L, versions 1-10, the smallest
    /// that fits). Pure C# with no Unity types so it is trivially testable and IL2CPP-safe. Returns the module
    /// matrix (true = dark); the caller turns it into pixels. Enough capacity for a connect URL (up to about
    /// 271 bytes at level L). Vendored from the standalone library DooDesch/QrLite (class renamed); keep in sync.
    /// </summary>
    internal static class QrCode
    {
        // Error-correction characteristics for level L, versions 1..10: (ecPerBlock, g1Blocks, g1Data, g2Blocks, g2Data).
        private static readonly int[][] EccL =
        {
            new[] { 7, 1, 19, 0, 0 },    // v1
            new[] { 10, 1, 34, 0, 0 },   // v2
            new[] { 15, 1, 55, 0, 0 },   // v3
            new[] { 20, 1, 80, 0, 0 },   // v4
            new[] { 26, 1, 108, 0, 0 },  // v5
            new[] { 18, 2, 68, 0, 0 },   // v6
            new[] { 20, 2, 78, 0, 0 },   // v7
            new[] { 24, 2, 97, 0, 0 },   // v8
            new[] { 30, 2, 116, 0, 0 },  // v9
            new[] { 18, 2, 68, 2, 69 },  // v10
        };

        // Alignment-pattern centre coordinates per version (1..10). v1 has none.
        private static readonly int[][] AlignPos =
        {
            new int[] { },
            new[] { 6, 18 },
            new[] { 6, 22 },
            new[] { 6, 26 },
            new[] { 6, 30 },
            new[] { 6, 34 },
            new[] { 6, 22, 38 },
            new[] { 6, 24, 42 },
            new[] { 6, 26, 46 },
            new[] { 6, 28, 50 },
        };

        /// <summary>Encode <paramref name="text"/> (UTF-8, byte mode) to an NxN module matrix (true = dark), or
        /// null if it needs more than version 10 at level L (about 271 bytes).</summary>
        internal static bool[,] Encode(string text)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(text ?? "");

            int version = -1;
            for (int v = 1; v <= 10; v++)
            {
                int cci = v <= 9 ? 8 : 16;
                int needBits = 4 + cci + data.Length * 8;
                int[] e = EccL[v - 1];
                int dataCw = e[1] * e[2] + e[3] * e[4];
                if (needBits <= dataCw * 8) { version = v; break; }
            }
            if (version < 1) return null;

            byte[] codewords = BuildCodewords(data, version);
            int size = 17 + 4 * version;
            var modules = new int[size, size];   // -1 unset, 0 light, 1 dark
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    modules[r, c] = -1;
            var isFunction = new bool[size, size];

            DrawFunctionPatterns(modules, isFunction, version, size);
            DrawCodewords(modules, isFunction, codewords, size);

            int bestPenalty = int.MaxValue;
            int[,] bestModules = null;
            for (int mask = 0; mask < 8; mask++)
            {
                int[,] trial = (int[,])modules.Clone();
                ApplyMask(trial, isFunction, mask, size);
                DrawFormatBits(trial, version, mask, size);
                int p = Penalty(trial, size);
                if (p < bestPenalty) { bestPenalty = p; bestModules = trial; }
            }

            var result = new bool[size, size];
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    result[r, c] = bestModules[r, c] == 1;
            return result;
        }

        // ----- data + error-correction codewords -----

        private static byte[] BuildCodewords(byte[] data, int version)
        {
            int[] e = EccL[version - 1];
            int ecPerBlock = e[0], g1 = e[1], g1d = e[2], g2 = e[3], g2d = e[4];
            int totalBlocks = g1 + g2;
            int dataCw = g1 * g1d + g2 * g2d;

            // Bit stream: mode (byte=0100), char count, data, terminator, pad to byte, pad bytes.
            var bits = new BitBuffer();
            bits.Append(0b0100, 4);
            bits.Append(data.Length, version <= 9 ? 8 : 16);
            foreach (byte b in data) bits.Append(b, 8);

            int capacity = dataCw * 8;
            int terminator = Math.Min(4, capacity - bits.Length);
            bits.Append(0, terminator);
            while (bits.Length % 8 != 0) bits.Append(0, 1);
            byte pad = 0xEC;
            while (bits.Length < capacity) { bits.Append(pad, 8); pad = (byte)(pad == 0xEC ? 0x11 : 0xEC); }

            byte[] dataBytes = bits.ToBytes();

            // Split into blocks, compute EC per block.
            var dataBlocks = new byte[totalBlocks][];
            var ecBlocks = new byte[totalBlocks][];
            int[] gen = RsGenerator(ecPerBlock);
            int offset = 0;
            for (int b = 0; b < totalBlocks; b++)
            {
                int len = b < g1 ? g1d : g2d;
                var block = new byte[len];
                Array.Copy(dataBytes, offset, block, 0, len);
                offset += len;
                dataBlocks[b] = block;
                ecBlocks[b] = RsEncode(block, gen);
            }

            // Interleave data codewords, then EC codewords.
            var outCw = new List<byte>(dataCw + totalBlocks * ecPerBlock);
            int maxData = Math.Max(g1d, g2d);
            for (int i = 0; i < maxData; i++)
                for (int b = 0; b < totalBlocks; b++)
                    if (i < dataBlocks[b].Length) outCw.Add(dataBlocks[b][i]);
            for (int i = 0; i < ecPerBlock; i++)
                for (int b = 0; b < totalBlocks; b++)
                    outCw.Add(ecBlocks[b][i]);
            return outCw.ToArray();
        }

        // ----- GF(256) Reed-Solomon -----

        private static readonly int[] Exp = new int[256];
        private static readonly int[] Log = new int[256];

        static QrCode()
        {
            int x = 1;
            for (int i = 0; i < 256; i++)
            {
                Exp[i] = x;
                if (i < 255) Log[x] = i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D;
            }
        }

        private static int Mul(int a, int b) => a == 0 || b == 0 ? 0 : Exp[(Log[a] + Log[b]) % 255];

        private static int[] RsGenerator(int degree)
        {
            var gen = new int[] { 1 };
            for (int i = 0; i < degree; i++)
                gen = MulPoly(gen, new[] { 1, Exp[i] });   // multiply by (x - a^i), a^i = Exp[i]
            return gen;
        }

        private static int[] MulPoly(int[] a, int[] b)
        {
            var r = new int[a.Length + b.Length - 1];
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    r[i + j] ^= Mul(a[i], b[j]);
            return r;
        }

        private static byte[] RsEncode(byte[] data, int[] gen)
        {
            int ecLen = gen.Length - 1;
            var res = new int[data.Length + ecLen];
            for (int i = 0; i < data.Length; i++) res[i] = data[i];
            for (int i = 0; i < data.Length; i++)
            {
                int coef = res[i];
                if (coef == 0) continue;
                for (int j = 0; j < gen.Length; j++)
                    res[i + j] ^= Mul(gen[j], coef);
            }
            var ec = new byte[ecLen];
            for (int i = 0; i < ecLen; i++) ec[i] = (byte)res[data.Length + i];
            return ec;
        }

        // ----- matrix layout -----

        private static void DrawFunctionPatterns(int[,] m, bool[,] fn, int version, int size)
        {
            // Timing patterns
            for (int i = 0; i < size; i++)
            {
                Set(m, fn, 6, i, i % 2 == 0);
                Set(m, fn, i, 6, i % 2 == 0);
            }
            // Finder patterns + separators
            DrawFinder(m, fn, 0, 0, size);
            DrawFinder(m, fn, size - 7, 0, size);
            DrawFinder(m, fn, 0, size - 7, size);
            // Alignment patterns
            int[] pos = AlignPos[version - 1];
            for (int i = 0; i < pos.Length; i++)
                for (int j = 0; j < pos.Length; j++)
                {
                    int r = pos[i], c = pos[j];
                    if ((r == 6 && c == 6) || (r == 6 && c == size - 7) || (r == size - 7 && c == 6)) continue;
                    DrawAlignment(m, fn, r, c);
                }
            // Dark module + reserved format/version areas (marked as function so data skips them).
            Set(m, fn, size - 8, 8, true);
            ReserveFormat(fn, size);
            if (version >= 7) ReserveVersion(fn, size);
        }

        private static void DrawFinder(int[,] m, bool[,] fn, int row, int col, int size)
        {
            for (int r = -1; r <= 7; r++)
                for (int c = -1; c <= 7; c++)
                {
                    int rr = row + r, cc = col + c;
                    if (rr < 0 || rr >= size || cc < 0 || cc >= size) continue;
                    bool dark = r >= 0 && r <= 6 && c >= 0 && c <= 6 &&
                                (r == 0 || r == 6 || c == 0 || c == 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4));
                    Set(m, fn, rr, cc, dark);
                }
        }

        private static void DrawAlignment(int[,] m, bool[,] fn, int row, int col)
        {
            for (int r = -2; r <= 2; r++)
                for (int c = -2; c <= 2; c++)
                    Set(m, fn, row + r, col + c, Math.Max(Math.Abs(r), Math.Abs(c)) != 1);
        }

        private static void ReserveFormat(bool[,] fn, int size)
        {
            // Exactly the cells the two format-info copies occupy (must match DrawFormatBits so data placement
            // skips precisely these). Vertical copy on col 8, horizontal copy on row 8.
            for (int i = 0; i < 6; i++) { fn[i, 8] = true; fn[8, i] = true; }   // rows/cols 0-5
            fn[7, 8] = true; fn[8, 7] = true; fn[8, 8] = true;
            for (int i = 0; i < 8; i++) { fn[size - 1 - i, 8] = true; fn[8, size - 1 - i] = true; }   // size-1..size-8
        }

        private static void ReserveVersion(bool[,] fn, int size)
        {
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 3; j++)
                {
                    fn[size - 11 + j, i] = true;
                    fn[i, size - 11 + j] = true;
                }
        }

        private static void DrawCodewords(int[,] m, bool[,] fn, byte[] cw, int size)
        {
            int bit = 0;
            int total = cw.Length * 8;
            for (int col = size - 1; col > 0; col -= 2)
            {
                if (col == 6) col = 5;   // skip the vertical timing column
                for (int t = 0; t < size; t++)
                {
                    bool upward = ((col + 1) & 2) == 0;
                    int row = upward ? size - 1 - t : t;
                    for (int k = 0; k < 2; k++)
                    {
                        int c = col - k;
                        if (fn[row, c]) continue;
                        bool dark = false;
                        if (bit < total) dark = ((cw[bit >> 3] >> (7 - (bit & 7))) & 1) == 1;
                        m[row, c] = dark ? 1 : 0;
                        bit++;
                    }
                }
            }
        }

        private static void ApplyMask(int[,] m, bool[,] fn, int mask, int size)
        {
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                {
                    if (fn[r, c]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (r + c) % 2 == 0; break;
                        case 1: invert = r % 2 == 0; break;
                        case 2: invert = c % 3 == 0; break;
                        case 3: invert = (r + c) % 3 == 0; break;
                        case 4: invert = (r / 2 + c / 3) % 2 == 0; break;
                        case 5: invert = (r * c) % 2 + (r * c) % 3 == 0; break;
                        case 6: invert = ((r * c) % 2 + (r * c) % 3) % 2 == 0; break;
                        default: invert = ((r + c) % 2 + (r * c) % 3) % 2 == 0; break;
                    }
                    if (invert) m[r, c] ^= 1;
                }
        }

        private static void DrawFormatBits(int[,] m, int version, int mask, int size)
        {
            // Format info: 2-bit ECC level (L = 01) + 3-bit mask, BCH(15,5), XOR mask 0x5412. Placed LSB-first in
            // two copies (vertical on col 8, horizontal on row 8), mirroring the standard layout exactly.
            int fmt = (0b01 << 3) | mask;
            int rem = fmt;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((fmt << 10) | rem) ^ 0x5412;

            for (int i = 0; i < 15; i++)
            {
                int mod = Bit(bits, i);
                // vertical (col 8)
                if (i < 6) SetVal(m, i, 8, mod);
                else if (i < 8) SetVal(m, i + 1, 8, mod);
                else SetVal(m, size - 15 + i, 8, mod);
                // horizontal (row 8)
                if (i < 8) SetVal(m, 8, size - i - 1, mod);
                else if (i < 9) SetVal(m, 8, 7, mod);
                else SetVal(m, 8, 15 - i - 1, mod);
            }
            SetVal(m, size - 8, 8, 1);   // dark module

            if (version >= 7)
            {
                int vrem = version;
                for (int i = 0; i < 12; i++) vrem = (vrem << 1) ^ ((vrem >> 11) * 0x1F25);
                int vbits = (version << 12) | vrem;
                for (int i = 0; i < 18; i++)
                {
                    int b = Bit(vbits, i);
                    int a = i / 3, c = i % 3;
                    SetVal(m, size - 11 + c, a, b);
                    SetVal(m, a, size - 11 + c, b);
                }
            }
        }

        // ----- mask penalty scoring -----

        private static int Penalty(int[,] m, int size)
        {
            int penalty = 0;

            // Rule 1: runs of 5+ same-colour in row/column.
            for (int r = 0; r < size; r++)
            {
                int runColor = -1, runLen = 0;
                for (int c = 0; c < size; c++)
                {
                    int v = m[r, c];
                    if (v == runColor) { runLen++; if (runLen == 5) penalty += 3; else if (runLen > 5) penalty++; }
                    else { runColor = v; runLen = 1; }
                }
            }
            for (int c = 0; c < size; c++)
            {
                int runColor = -1, runLen = 0;
                for (int r = 0; r < size; r++)
                {
                    int v = m[r, c];
                    if (v == runColor) { runLen++; if (runLen == 5) penalty += 3; else if (runLen > 5) penalty++; }
                    else { runColor = v; runLen = 1; }
                }
            }

            // Rule 2: 2x2 blocks of the same colour.
            for (int r = 0; r < size - 1; r++)
                for (int c = 0; c < size - 1; c++)
                {
                    int v = m[r, c];
                    if (v == m[r, c + 1] && v == m[r + 1, c] && v == m[r + 1, c + 1]) penalty += 3;
                }

            // Rule 3: finder-like 1:1:3:1:1 patterns in rows and columns.
            int[] p1 = { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
            int[] p2 = { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };
            for (int r = 0; r < size; r++)
                for (int c = 0; c <= size - 11; c++)
                {
                    if (MatchPattern(m, r, c, true, p1) || MatchPattern(m, r, c, true, p2)) penalty += 40;
                }
            for (int c = 0; c < size; c++)
                for (int r = 0; r <= size - 11; r++)
                {
                    if (MatchPattern(m, r, c, false, p1) || MatchPattern(m, r, c, false, p2)) penalty += 40;
                }

            // Rule 4: overall dark/light balance.
            int dark = 0;
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (m[r, c] == 1) dark++;
            int total = size * size;
            int percent = dark * 100 / total;
            int k = Math.Abs(percent - 50) / 5;
            penalty += k * 10;

            return penalty;
        }

        private static bool MatchPattern(int[,] m, int r, int c, bool horizontal, int[] pat)
        {
            for (int i = 0; i < 11; i++)
            {
                int v = horizontal ? m[r, c + i] : m[r + i, c];
                if (v != pat[i]) return false;
            }
            return true;
        }

        // ----- helpers -----

        private static void Set(int[,] m, bool[,] fn, int r, int c, bool dark) { m[r, c] = dark ? 1 : 0; fn[r, c] = true; }
        private static void SetVal(int[,] m, int r, int c, int v) => m[r, c] = v;
        private static int Bit(int value, int i) => (value >> i) & 1;

        private sealed class BitBuffer
        {
            private readonly List<byte> _bytes = new List<byte>();
            private int _bitLen;
            internal int Length => _bitLen;
            internal void Append(int value, int bits)
            {
                for (int i = bits - 1; i >= 0; i--)
                {
                    if (_bitLen % 8 == 0) _bytes.Add(0);
                    int bit = (value >> i) & 1;
                    if (bit != 0) _bytes[_bitLen / 8] |= (byte)(1 << (7 - (_bitLen % 8)));
                    _bitLen++;
                }
            }
            internal byte[] ToBytes() => _bytes.ToArray();
        }
    }
}
