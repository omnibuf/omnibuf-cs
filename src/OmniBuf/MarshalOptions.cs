namespace OmniBuf;

/// <summary>
/// Options controlling OmniBuf binary serialization.
/// </summary>
public readonly record struct MarshalOptions
{
    private readonly bool _nonDeterministic;

    public MarshalOptions(bool SelfDescribing = false, bool Deterministic = true)
    {
        this.SelfDescribing = SelfDescribing;
        _nonDeterministic = !Deterministic;
    }

    public bool SelfDescribing { get; init; }

    public bool Deterministic
    {
        get => !_nonDeterministic;
        init => _nonDeterministic = !value;
    }

    public static MarshalOptions Default => default;
}
