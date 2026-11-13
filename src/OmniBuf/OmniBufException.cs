namespace OmniBuf;

/// <summary>
/// Exception thrown when an OmniBuf payload or schema violates format invariants.
/// </summary>
public sealed class OmniBufException : Exception
{
    public OmniBufException(string message)
        : base(message)
    {
    }

    public OmniBufException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
