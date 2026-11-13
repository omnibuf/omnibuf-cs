namespace OmniBuf;

/// <summary>
/// Options controlling OmniBuf binary serialization.
/// </summary>
public readonly record struct MarshalOptions(bool SelfDescribing = false, bool Deterministic = true)
{
    public static MarshalOptions Default => new(SelfDescribing: false, Deterministic: true);
}
