namespace OmniBuf;

/// <summary>
/// Marks a C# class, record, or struct as an OmniBuf serializable message.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class OmniBufMessageAttribute : Attribute
{
    public string? TypeName { get; set; }
}

/// <summary>
/// Maps a C# property or constructor parameter to an OmniBuf field number and wire representation.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true)]
public sealed class OmniBufFieldAttribute : Attribute
{
    private OmniBufWireType _wire;

    public OmniBufFieldAttribute(uint fieldNumber)
    {
        FieldNumber = fieldNumber;
    }

    public uint FieldNumber { get; }

    public string? Name { get; set; }

    public OmniBufWireType Wire
    {
        get => _wire;
        set
        {
            _wire = value;
            HasExplicitWire = true;
        }
    }

    public bool HasExplicitWire { get; private set; }

    public OmniBufWireType KeyWire
    {
        get => _keyWire;
        set
        {
            _keyWire = value;
            HasExplicitKeyWire = true;
        }
    }

    private OmniBufWireType _keyWire;

    public bool HasExplicitKeyWire { get; private set; }

    public OmniBufWireType ValueWire
    {
        get => _valueWire;
        set
        {
            _valueWire = value;
            HasExplicitValueWire = true;
        }
    }

    private OmniBufWireType _valueWire;

    public bool HasExplicitValueWire { get; private set; }

    public bool OmitEmpty { get; set; }
}
