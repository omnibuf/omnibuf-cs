using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace OmniBuf;

/// <summary>
/// Resource limits controlling OmniBuf binary deserialization and structural validation.
/// </summary>
public readonly record struct UnmarshalOptions
{
    /// <summary>
    /// Default maximum top-level packet byte length (64 MiB).
    /// </summary>
    public const int DefaultMaxMessageBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Default maximum element count for a collection (1,000,000).
    /// </summary>
    public const int DefaultMaxCollectionElements = 1_000_000;

    /// <summary>
    /// Default maximum container nesting depth (<see cref="OmniBufConstants.MaxNestingDepth"/>).
    /// </summary>
    public const int DefaultMaxNestingDepth = OmniBufConstants.MaxNestingDepth;

    private const byte HasMaxMessageBytesMask = 1;
    private const byte HasMaxCollectionElementsMask = 2;
    private const byte HasMaxNestingDepthMask = 4;

    private readonly byte _setMask;
    private readonly int _maxMessageBytes;
    private readonly int _maxCollectionElements;
    private readonly int _maxNestingDepth;

    public UnmarshalOptions(
        int MaxMessageBytes = DefaultMaxMessageBytes,
        int MaxCollectionElements = DefaultMaxCollectionElements,
        int MaxNestingDepth = DefaultMaxNestingDepth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxCollectionElements);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxNestingDepth);

        _maxMessageBytes = MaxMessageBytes;
        _maxCollectionElements = MaxCollectionElements;
        _maxNestingDepth = MaxNestingDepth;
        _setMask = HasMaxMessageBytesMask | HasMaxCollectionElementsMask | HasMaxNestingDepthMask;
    }

    public int MaxMessageBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_setMask & HasMaxMessageBytesMask) != 0 ? _maxMessageBytes : DefaultMaxMessageBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxMessageBytes = value;
            _setMask |= HasMaxMessageBytesMask;
        }
    }

    public int MaxCollectionElements
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_setMask & HasMaxCollectionElementsMask) != 0 ? _maxCollectionElements : DefaultMaxCollectionElements;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxCollectionElements = value;
            _setMask |= HasMaxCollectionElementsMask;
        }
    }

    public int MaxNestingDepth
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (_setMask & HasMaxNestingDepthMask) != 0 ? _maxNestingDepth : DefaultMaxNestingDepth;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxNestingDepth = value;
            _setMask |= HasMaxNestingDepthMask;
        }
    }

    public static UnmarshalOptions Default => default;

    public bool Equals(UnmarshalOptions other) =>
        MaxMessageBytes == other.MaxMessageBytes
        && MaxCollectionElements == other.MaxCollectionElements
        && MaxNestingDepth == other.MaxNestingDepth;

    public override int GetHashCode() =>
        HashCode.Combine(MaxMessageBytes, MaxCollectionElements, MaxNestingDepth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ValidateMessageLength(int length)
    {
        int maxBytes = MaxMessageBytes;
        if (length > maxBytes)
        {
            ThrowMessageLengthExceeded(length, maxBytes);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ValidateCollectionCount(long count)
    {
        int maxElems = MaxCollectionElements;
        if (count > maxElems)
        {
            ThrowCollectionCountExceeded(count, maxElems);
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowMessageLengthExceeded(int length, int maxBytes) =>
        throw new OmniBufException($"omnibuf: message length {length} exceeds maximum {maxBytes}");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCollectionCountExceeded(long count, int maxElems) =>
        throw new OmniBufException($"omnibuf: collection element count {count} exceeds maximum {maxElems}");
}
