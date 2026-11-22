using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.Unicode;

namespace OmniBuf;

/// <summary>
/// 4-bit self-describing OmniBuf wire types carried in the low nibble of every field tag.
/// </summary>
public enum OmniBufWireType : byte
{
    Uint = 0,
    Sint = 1,
    BoolFalse = 2,
    BoolTrue = 3,
    Fixed32 = 4,
    Fixed64 = 5,
    Sfixed32 = 6,
    Sfixed64 = 7,
    Float32 = 8,
    Float64 = 9,
    String = 10,
    Bytes = 11,
    SubMsg = 12,
    Array = 13,
    Map = 14,
    Extended = 15,
}

/// <summary>
/// Core constants and bit-level wire helpers for OmniBuf.
/// </summary>
public static class OmniBufConstants
{
    public const uint MaxFieldNumber = 536_870_911u; // (1 << 29) - 1
    public const uint ReservedFieldMin = 19_000u;
    public const uint ReservedFieldMax = 19_999u;
    public const int MaxNestingDepth = 32;
    public const byte Field0SubMsgTag = 0x19; // (((0 << 4) | 12) << 1) | 1

    internal static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Latin1 = Encoding.Latin1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ZigZagEncode64(long n) => (ulong)((n << 1) ^ (n >> 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ZigZagDecode64(ulong u) => (long)(u >> 1) ^ -(long)(u & 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ZigZagEncode32(int n) => (uint)((n << 1) ^ (n >> 31));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ZigZagDecode32(uint u) => (int)(u >> 1) ^ -(int)(u & 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidContainerElemType(OmniBufWireType wt) =>
        (byte)wt <= (byte)OmniBufWireType.Extended && wt != OmniBufWireType.BoolTrue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValidMapKeyType(OmniBufWireType wt) =>
        wt is OmniBufWireType.Uint
            or OmniBufWireType.Sint
            or OmniBufWireType.BoolFalse
            or OmniBufWireType.Fixed32
            or OmniBufWireType.Fixed64
            or OmniBufWireType.Sfixed32
            or OmniBufWireType.Sfixed64
            or OmniBufWireType.String;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FixedElementWidth(OmniBufWireType wt) => wt switch
    {
        OmniBufWireType.BoolFalse => 1,
        OmniBufWireType.Fixed32 or OmniBufWireType.Sfixed32 or OmniBufWireType.Float32 => 4,
        OmniBufWireType.Fixed64 or OmniBufWireType.Sfixed64 or OmniBufWireType.Float64 => 8,
        _ => 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsUserFieldNumberValid(uint fieldNum) =>
        fieldNum >= 1
        && fieldNum <= MaxFieldNumber
        && (fieldNum < ReservedFieldMin || fieldNum > ReservedFieldMax);

    [ThreadStatic]
    private static char[]? t_utf16Scratch;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char[] GetThreadLocalCharScratch() => t_utf16Scratch ??= new char[256];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string DecodeUtf8Strict(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        return DecodeUtf8StrictNonEmpty(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe string DecodeUtf8StrictNonEmpty(ReadOnlySpan<byte> bytes)
    {
        int len = bytes.Length;
        if (len <= 512)
        {
            fixed (byte* ptr = &MemoryMarshal.GetReference(bytes))
            {
                return DecodeUtf8StrictPinnedShort(ptr, len);
            }
        }

        if (Ascii.IsValid(bytes))
        {
            return Latin1.GetString(bytes);
        }

        return DecodeUtf8StrictNonAscii(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe string DecodeUtf8StrictPinned(byte* ptr, int len)
    {
        if (len <= 512)
        {
            return DecodeUtf8StrictPinnedShort(ptr, len);
        }

        ReadOnlySpan<byte> bytes = new(ptr, len);
        if (Ascii.IsValid(bytes))
        {
            return Latin1.GetString(bytes);
        }

        return DecodeUtf8StrictNonAscii(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe string DecodeUtf8StrictPinnedShort(byte* ptr, int len)
    {
        bool ok = true;
        string s = string.Create(len, ((nint)ptr, (nint)(&ok)), static (dst, state) =>
        {
            if (!TryWidenAsciiFast((byte*)state.Item1, ref MemoryMarshal.GetReference(dst), dst.Length))
            {
                *(bool*)state.Item2 = false;
            }
        });
        return ok ? s : DecodeUtf8StrictNonAscii(new ReadOnlySpan<byte>(ptr, len));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool TryWidenAsciiFast(byte* src, ref char dst, int len)
    {
        ref ushort d = ref Unsafe.As<char, ushort>(ref dst);
        if (Vector128.IsHardwareAccelerated && len >= 16)
        {
            int i = 0;
            int end16 = len - 16;
            while (true)
            {
                Vector128<byte> v = Vector128.Load(src + i);
                if (Vector128.LessThanAny(v.AsSByte(), Vector128<sbyte>.Zero))
                {
                    return false;
                }

                (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(v);
                lo.StoreUnsafe(ref d, (nuint)i);
                hi.StoreUnsafe(ref d, (nuint)(i + 8));
                if (i == end16)
                {
                    return true;
                }

                i += 16;
                if (i > end16)
                {
                    i = end16;
                }
            }
        }

        if (Vector64.IsHardwareAccelerated && len >= 8 && len < 16)
        {
            Vector64<byte> v0 = Vector64.Load(src);
            Vector64<byte> v1 = Vector64.Load(src + len - 8);
            if (Vector64.LessThanAny((v0 | v1).AsSByte(), Vector64<sbyte>.Zero))
            {
                return false;
            }

            Vector128.WidenLower(v0.ToVector128Unsafe()).StoreUnsafe(ref d, 0);
            Vector128.WidenLower(v1.ToVector128Unsafe()).StoreUnsafe(ref d, (nuint)(len - 8));
            return true;
        }

        if (len >= 4 && len < 8)
        {
            uint u0 = Unsafe.ReadUnaligned<uint>(src);
            uint u1 = Unsafe.ReadUnaligned<uint>(src + len - 4);
            if (!BitConverter.IsLittleEndian)
            {
                u0 = BinaryPrimitives.ReverseEndianness(u0);
                u1 = BinaryPrimitives.ReverseEndianness(u1);
            }

            if (((u0 | u1) & 0x80808080u) != 0)
            {
                return false;
            }

            Unsafe.Add(ref d, 0) = (byte)u0;
            Unsafe.Add(ref d, 1) = (byte)(u0 >> 8);
            Unsafe.Add(ref d, 2) = (byte)(u0 >> 16);
            Unsafe.Add(ref d, 3) = (byte)(u0 >> 24);
            int tail = len - 4;
            Unsafe.Add(ref d, tail) = (byte)u1;
            Unsafe.Add(ref d, tail + 1) = (byte)(u1 >> 8);
            Unsafe.Add(ref d, tail + 2) = (byte)(u1 >> 16);
            Unsafe.Add(ref d, tail + 3) = (byte)(u1 >> 24);
            return true;
        }

        uint orAll = 0;
        for (int i = 0; i < len; i++)
        {
            byte b = src[i];
            orAll |= b;
            Unsafe.Add(ref d, i) = b;
        }

        return (orAll & 0x80u) == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string DecodeUtf8Strict(ReadOnlySpan<byte> bytes, char[] scratch) =>
        DecodeUtf8Strict(bytes);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DecodeUtf8StrictNonAscii(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            ThrowInvalidUtf8();
            return string.Empty;
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInvalidUtf8() =>
        throw new OmniBufException("omnibuf: invalid UTF-8 string payload");

    public static int GetUtf8ByteCountStrict(string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new OmniBufException("omnibuf: string contains invalid UTF-16 surrogates", ex);
        }
    }
}

/// <summary>
/// Compares UTF-16 strings in exact UTF-8 lexicographical byte order (Unicode scalar value order).
/// </summary>
public sealed class Utf8OrdinalStringComparer : IComparer<string>
{
    public static readonly Utf8OrdinalStringComparer Instance = new();

    private Utf8OrdinalStringComparer()
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Compare(string? x, string? y) => CompareStatic(x, y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CompareStatic(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        ReadOnlySpan<char> sx = x.AsSpan();
        ReadOnlySpan<char> sy = y.AsSpan();
        int common = sx.CommonPrefixLength(sy);
        if (common == sx.Length || common == sy.Length)
        {
            return sx.Length.CompareTo(sy.Length);
        }

        if (common > 0 && char.IsHighSurrogate(sx[common - 1]))
        {
            common--;
        }

        char cx = sx[common];
        char cy = sy[common];
        if (cx < 0xD800 || cy < 0xD800)
        {
            return cx.CompareTo(cy);
        }

        return CompareSurrogateOrBmpTail(sx.Slice(common), sy.Slice(common), cx, cy);
    }

    private static int CompareSurrogateOrBmpTail(ReadOnlySpan<char> tx, ReadOnlySpan<char> ty, char cx, char cy)
    {
        if (Rune.DecodeFromUtf16(tx, out Rune rx, out _) == OperationStatus.Done
            && Rune.DecodeFromUtf16(ty, out Rune ry, out _) == OperationStatus.Done)
        {
            return rx.Value.CompareTo(ry.Value);
        }

        return cx.CompareTo(cy);
    }
}
