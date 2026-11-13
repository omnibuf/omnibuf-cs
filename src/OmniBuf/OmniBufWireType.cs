using System.Runtime.CompilerServices;
using System.Text;

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

    public static string DecodeUtf8Strict(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new OmniBufException("omnibuf: invalid UTF-8 string payload", ex);
        }
    }

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
