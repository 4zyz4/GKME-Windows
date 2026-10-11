using System;
using System.Collections.Generic;
using System.Text;

namespace GKME.Core;

/// <summary>Error correction level of a QR Code.</summary>
public enum QrEcc
{
    Low = 0,
    Medium = 1,
    Quartile = 2,
    High = 3,
}

/// <summary>
/// Minimal, dependency-free QR Code (model 2) encoder producing a square
/// module grid. Supports byte mode and versions 1..40 with automatic version
/// and mask selection. The rendering of the grid is left to the caller.
///
/// The algorithm is a port of the reference implementation by Project Nayuki
/// (https://www.nayuki.io/page/qr-code-generator-library), which is released
/// under the MIT License.
/// </summary>
public sealed class QrCode
{
    public const int MinVersion = 1;
    public const int MaxVersion = 40;

    // Scanned in index order; index 0 is a placeholder so versions line up.
    private static readonly int[][] EccCodewordsPerBlock =
    {
        new[] { -1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28 },
        new[] { -1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { -1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30 },
    };

    private static readonly int[][] NumEccBlocks =
    {
        new[] { -1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25 },
        new[] { -1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49 },
        new[] { -1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68 },
        new[] { -1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81 },
    };

    public int Version { get; }
    public QrEcc ErrorCorrectionLevel { get; }
    public int Size { get; }
    public int Mask { get; }

    private readonly bool[][] _modules;
    private readonly bool[][] _isFunction;

    /// <summary>Encodes <paramref name="text"/> as UTF-8 using byte mode and
    /// the smallest version that fits.</summary>
    public static QrCode EncodeText(string text, QrEcc ecl)
    {
        var data = Encoding.UTF8.GetBytes(text ?? "");

        int version = MinVersion;
        for (; version <= MaxVersion; version++)
        {
            int capacityBits = GetNumDataCodewords(version, ecl) * 8;
            int usedBits = 4 + ByteModeCharCountBits(version) + data.Length * 8;
            if (usedBits <= capacityBits) break;
        }
        if (version > MaxVersion)
            throw new ArgumentException("数据过长，无法生成二维码", nameof(text));

        var bb = new BitBuffer();
        bb.AppendBits(0x4, 4);                              // Byte mode indicator
        bb.AppendBits((uint)data.Length, ByteModeCharCountBits(version));
        foreach (byte b in data) bb.AppendBits(b, 8);

        int dataCapacityBits = GetNumDataCodewords(version, ecl) * 8;
        bb.AppendBits(0, Math.Min(4, dataCapacityBits - bb.Length)); // Terminator
        bb.AppendBits(0, (8 - bb.Length % 8) % 8);                   // Byte align
        for (int padByte = 0xEC; bb.Length < dataCapacityBits; padByte ^= 0xEC ^ 0x11)
            bb.AppendBits((uint)padByte, 8);

        return new QrCode(version, ecl, bb.ToBytes(), -1);
    }

    private QrCode(int version, QrEcc ecl, byte[] dataCodewords, int mask)
    {
        if (version < MinVersion || version > MaxVersion)
            throw new ArgumentOutOfRangeException(nameof(version));
        if (dataCodewords.Length != GetNumDataCodewords(version, ecl))
            throw new ArgumentException("数据长度与版本不符", nameof(dataCodewords));
        if (mask < -1 || mask > 7)
            throw new ArgumentOutOfRangeException(nameof(mask));

        Version = version;
        ErrorCorrectionLevel = ecl;
        Size = version * 4 + 17;

        _modules = new bool[Size][];
        _isFunction = new bool[Size][];
        for (int i = 0; i < Size; i++)
        {
            _modules[i] = new bool[Size];
            _isFunction[i] = new bool[Size];
        }

        DrawFunctionPatterns();
        byte[] allCodewords = AddEccAndInterleave(dataCodewords);
        DrawCodewords(allCodewords);

        if (mask == -1)
        {
            int bestMask = 0;
            int minPenalty = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                ApplyMask(i);
                DrawFormatBits(i);
                int penalty = GetPenaltyScore();
                if (penalty < minPenalty)
                {
                    bestMask = i;
                    minPenalty = penalty;
                }
                ApplyMask(i); // Undo
            }
            mask = bestMask;
        }

        Mask = mask;
        ApplyMask(mask);
        DrawFormatBits(mask);
    }

    public bool GetModule(int x, int y)
    {
        if (x < 0 || x >= Size || y < 0 || y >= Size) return false;
        return _modules[y][x];
    }

    // ────────────────────────── Capacity helpers ──────────────────────────

    private static int GetNumRawDataModules(int ver)
    {
        int result = (16 * ver + 128) * ver + 64;
        if (ver >= 2)
        {
            int numAlign = ver / 7 + 2;
            result -= (25 * numAlign - 10) * numAlign - 55;
            if (ver >= 7) result -= 36;
        }
        return result;
    }

    private static int GetNumDataCodewords(int ver, QrEcc ecl)
        => GetNumRawDataModules(ver) / 8
           - EccCodewordsPerBlock[(int)ecl][ver] * NumEccBlocks[(int)ecl][ver];

    private static int ByteModeCharCountBits(int version) => version <= 9 ? 8 : 16;

    // ────────────────────────── Function patterns ──────────────────────────

    private void DrawFunctionPatterns()
    {
        for (int i = 0; i < Size; i++)
        {
            SetFunctionModule(6, i, i % 2 == 0);
            SetFunctionModule(i, 6, i % 2 == 0);
        }

        DrawFinderPattern(3, 3);
        DrawFinderPattern(Size - 4, 3);
        DrawFinderPattern(3, Size - 4);

        int[] alignPatPos = GetAlignmentPatternPositions();
        int numAlign = alignPatPos.Length;
        for (int i = 0; i < numAlign; i++)
        {
            for (int j = 0; j < numAlign; j++)
            {
                if (!((i == 0 && j == 0) || (i == 0 && j == numAlign - 1) || (i == numAlign - 1 && j == 0)))
                    DrawAlignmentPattern(alignPatPos[i], alignPatPos[j]);
            }
        }

        DrawFormatBits(0);
        DrawVersion();
    }

    private void DrawFinderPattern(int x, int y)
    {
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                int xx = x + dx, yy = y + dy;
                if (xx >= 0 && xx < Size && yy >= 0 && yy < Size)
                    SetFunctionModule(xx, yy, dist != 2 && dist != 4);
            }
        }
    }

    private void DrawAlignmentPattern(int x, int y)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                SetFunctionModule(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
    }

    private int[] GetAlignmentPatternPositions()
    {
        if (Version == 1) return Array.Empty<int>();
        int numAlign = Version / 7 + 2;
        int step = Version == 32
            ? 26
            : (Version * 4 + numAlign * 2 + 1) / (numAlign * 2 - 2) * 2;
        var result = new int[numAlign];
        result[0] = 6;
        for (int i = numAlign - 1, pos = Size - 7; i >= 1; i--, pos -= step)
            result[i] = pos;
        return result;
    }

    private void SetFunctionModule(int x, int y, bool isDark)
    {
        _modules[y][x] = isDark;
        _isFunction[y][x] = true;
    }

    // ────────────────────────── Format & version info ──────────────────────────

    // ISO/IEC 18004 format-info ECC field: L=01, M=00, Q=11, H=10.
    // This is deliberately not the table index used above.
    private static int FormatBits(QrEcc ecl) => ecl switch
    {
        QrEcc.Low => 1,
        QrEcc.Medium => 0,
        QrEcc.Quartile => 3,
        QrEcc.High => 2,
        _ => 0,
    };

    private void DrawFormatBits(int mask)
    {
        int data = FormatBits(ErrorCorrectionLevel) << 3 | mask;
        int rem = data;
        for (int i = 0; i < 10; i++)
            rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = (data << 10 | rem) ^ 0x5412;

        for (int i = 0; i <= 5; i++) SetFunctionModule(8, i, GetBit(bits, i));
        SetFunctionModule(8, 7, GetBit(bits, 6));
        SetFunctionModule(8, 8, GetBit(bits, 7));
        SetFunctionModule(7, 8, GetBit(bits, 8));
        for (int i = 9; i < 15; i++) SetFunctionModule(14 - i, 8, GetBit(bits, i));

        for (int i = 0; i < 8; i++) SetFunctionModule(Size - 1 - i, 8, GetBit(bits, i));
        for (int i = 8; i < 15; i++) SetFunctionModule(8, Size - 15 + i, GetBit(bits, i));
        SetFunctionModule(8, Size - 8, true);
    }

    private void DrawVersion()
    {
        if (Version < 7) return;
        int rem = Version;
        for (int i = 0; i < 12; i++)
            rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        int bits = Version << 12 | rem;

        for (int i = 0; i < 18; i++)
        {
            bool color = GetBit(bits, i);
            int a = Size - 11 + i % 3;
            int b = i / 3;
            SetFunctionModule(a, b, color);
            SetFunctionModule(b, a, color);
        }
    }

    // ────────────────────────── Codeword placement ──────────────────────────

    private void DrawCodewords(byte[] data)
    {
        if (data.Length != GetNumRawDataModules(Version) / 8)
            throw new ArgumentException("码字数量与版本不符", nameof(data));

        int i = 0;
        for (int right = Size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < Size; vert++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? Size - 1 - vert : vert;
                    if (!_isFunction[y][x] && i < data.Length * 8)
                    {
                        bool dark = GetBit(data[i >> 3], 7 - (i & 7));
                        _modules[y][x] = dark;
                        i++;
                    }
                }
            }
        }
    }

    private void ApplyMask(int mask)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                bool invert;
                switch (mask)
                {
                    case 0: invert = (x + y) % 2 == 0; break;
                    case 1: invert = y % 2 == 0; break;
                    case 2: invert = x % 3 == 0; break;
                    case 3: invert = (x + y) % 3 == 0; break;
                    case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                    case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                    case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                    case 7: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    default: throw new ArgumentOutOfRangeException(nameof(mask));
                }
                if (!_isFunction[y][x] && invert)
                    _modules[y][x] = !_modules[y][x];
            }
        }
    }

    // ────────────────────────── Error correction ──────────────────────────

    private byte[] AddEccAndInterleave(byte[] data)
    {
        int numBlocks = NumEccBlocks[(int)ErrorCorrectionLevel][Version];
        int blockEccLen = EccCodewordsPerBlock[(int)ErrorCorrectionLevel][Version];
        int rawCodewords = GetNumRawDataModules(Version) / 8;
        int numShortBlocks = numBlocks - rawCodewords % numBlocks;
        int shortBlockLen = rawCodewords / numBlocks;

        var blocks = new List<byte[]>();
        byte[] rsDiv = ReedSolomonComputeDivisor(blockEccLen);
        for (int i = 0, k = 0; i < numBlocks; i++)
        {
            int datLen = shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1);
            var dat = new byte[datLen];
            Array.Copy(data, k, dat, 0, datLen);
            k += datLen;
            byte[] ecc = ReedSolomonComputeRemainder(dat, rsDiv);
            if (i < numShortBlocks)
            {
                var padded = new byte[dat.Length + 1];
                Array.Copy(dat, padded, dat.Length);
                dat = padded; // trailing zero aligns the short block
            }
            var block = new byte[dat.Length + ecc.Length];
            Array.Copy(dat, block, dat.Length);
            Array.Copy(ecc, 0, block, dat.Length, ecc.Length);
            blocks.Add(block);
        }

        var result = new byte[rawCodewords];
        int idx = 0;
        for (int i = 0; i < blocks[0].Length; i++)
        {
            for (int j = 0; j < blocks.Count; j++)
            {
                if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
                    result[idx++] = blocks[j][i];
            }
        }
        return result;
    }

    private static byte[] ReedSolomonComputeDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)ReedSolomonMultiply(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = ReedSolomonMultiply(root, 0x02);
        }
        return result;
    }

    private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            byte factor = (byte)(b ^ result[0]);
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
                result[i] ^= (byte)ReedSolomonMultiply(divisor[i], factor);
        }
        return result;
    }

    private static int ReedSolomonMultiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    // ────────────────────────── Penalty scoring ──────────────────────────

    private const int PenaltyN1 = 3;
    private const int PenaltyN2 = 3;
    private const int PenaltyN3 = 40;
    private const int PenaltyN4 = 10;

    private int GetPenaltyScore()
    {
        int result = 0;

        for (int y = 0; y < Size; y++)
        {
            bool runColor = false;
            int runX = 0;
            var runHistory = new int[7];
            for (int x = 0; x < Size; x++)
            {
                if (_modules[y][x] == runColor)
                {
                    runX++;
                    if (runX == 5) result += PenaltyN1;
                    else if (runX > 5) result++;
                }
                else
                {
                    FinderPenaltyAddHistory(runX, runHistory);
                    if (!runColor)
                        result += FinderPenaltyCountPatterns(runHistory) * PenaltyN3;
                    runColor = _modules[y][x];
                    runX = 1;
                }
            }
            result += FinderPenaltyTerminateAndCount(runColor, runX, runHistory) * PenaltyN3;
        }

        for (int x = 0; x < Size; x++)
        {
            bool runColor = false;
            int runY = 0;
            var runHistory = new int[7];
            for (int y = 0; y < Size; y++)
            {
                if (_modules[y][x] == runColor)
                {
                    runY++;
                    if (runY == 5) result += PenaltyN1;
                    else if (runY > 5) result++;
                }
                else
                {
                    FinderPenaltyAddHistory(runY, runHistory);
                    if (!runColor)
                        result += FinderPenaltyCountPatterns(runHistory) * PenaltyN3;
                    runColor = _modules[y][x];
                    runY = 1;
                }
            }
            result += FinderPenaltyTerminateAndCount(runColor, runY, runHistory) * PenaltyN3;
        }

        for (int y = 0; y < Size - 1; y++)
        {
            for (int x = 0; x < Size - 1; x++)
            {
                bool color = _modules[y][x];
                if (color == _modules[y][x + 1] &&
                    color == _modules[y + 1][x] &&
                    color == _modules[y + 1][x + 1])
                    result += PenaltyN2;
            }
        }

        int dark = 0;
        foreach (bool[] row in _modules)
            foreach (bool color in row)
                if (color) dark++;
        int total = Size * Size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        result += k * PenaltyN4;

        return result;
    }

    private int FinderPenaltyCountPatterns(int[] runHistory)
    {
        int n = runHistory[1];
        bool core = n > 0 && runHistory[2] == n && runHistory[3] == n * 3 && runHistory[4] == n && runHistory[5] == n;
        return (core && runHistory[0] >= n * 4 && runHistory[6] >= n ? 1 : 0)
             + (core && runHistory[6] >= n * 4 && runHistory[0] >= n ? 1 : 0);
    }

    private int FinderPenaltyTerminateAndCount(bool currentRunColor, int currentRunLength, int[] runHistory)
    {
        if (currentRunColor)
        {
            FinderPenaltyAddHistory(currentRunLength, runHistory);
            currentRunLength = 0;
        }
        currentRunLength += Size;
        FinderPenaltyAddHistory(currentRunLength, runHistory);
        return FinderPenaltyCountPatterns(runHistory);
    }

    private void FinderPenaltyAddHistory(int currentRunLength, int[] runHistory)
    {
        if (runHistory[0] == 0)
            currentRunLength += Size;
        Array.Copy(runHistory, 0, runHistory, 1, runHistory.Length - 1);
        runHistory[0] = currentRunLength;
    }

    private static bool GetBit(int x, int i) => ((x >> i) & 1) != 0;

    // ────────────────────────── Bit buffer ──────────────────────────

    private sealed class BitBuffer
    {
        private readonly List<bool> _bits = new();
        public int Length => _bits.Count;

        public void AppendBits(uint val, int len)
        {
            for (int i = len - 1; i >= 0; i--)
                _bits.Add(((val >> i) & 1) != 0);
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[_bits.Count / 8];
            for (int i = 0; i < _bits.Count; i++)
                if (_bits[i]) bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            return bytes;
        }
    }
}
