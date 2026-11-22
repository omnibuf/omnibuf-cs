using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
        int len = bytes.Length;
        if (len == 0)
        {
            return string.Empty;
        }

        if (len <= 256)
        {
            return DecodeUtf8StrictWithScratch(bytes, t_utf16Scratch ??= new char[256]);
        }

        return DecodeUtf8StrictNonAscii(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string DecodeUtf8Strict(ReadOnlySpan<byte> bytes, char[] scratch)
    {
        int len = bytes.Length;
        if (len == 0)
        {
            return string.Empty;
        }

        if (len <= scratch.Length)
        {
            return DecodeUtf8StrictWithScratch(bytes, scratch);
        }

        return DecodeUtf8StrictNonAscii(bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string DecodeUtf8StrictWithScratch(ReadOnlySpan<byte> bytes, char[] scratch)
    {
        if (Ascii.ToUtf16(bytes, scratch, out int written) == OperationStatus.Done)
        {
            return new string(MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(scratch), written));
        }

        return DecodeUtf8NonAsciiScratch(bytes, scratch, written);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string DecodeUtf8NonAsciiScratch(ReadOnlySpan<byte> bytes, char[] scratch, int asciiWritten)
    {
        OperationStatus status = Utf8.ToUtf16(
            bytes.Slice(asciiWritten),
            scratch.AsSpan(asciiWritten),
            out _,
            out int remWritten,
            replaceInvalidSequences: false,
            isFinalBlock: true);
        if (status != OperationStatus.Done)
        {
            ThrowInvalidUtf8();
        }

        return new string(MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(scratch), asciiWritten + remWritten));
    }

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
