using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace OmniBuf;

/// <summary>
/// High-performance attribute-driven serializer for C# classes, structs, and records annotated with <see cref="OmniBufFieldAttribute"/>.
/// Compiles strongly typed property accessors and constructors once per type.
/// </summary>
public static class OmniBufSerializer
{
    private static readonly ConcurrentDictionary<Type, MessageCodec> Codecs = new();
    private static readonly object InitLock = new();

    public static byte[] Marshal<T>(T value, MarshalOptions options = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(value);
        OmniBufEncoder encoder = OmniBufEncoder.RentThreadLocal(out bool rented);
        try
        {
            Marshal(encoder, value, options);
            return encoder.ToArray();
        }
        finally
        {
            OmniBufEncoder.ReturnThreadLocal(encoder, rented);
        }
    }

    public static void Marshal<T>(OmniBufEncoder encoder, T value, MarshalOptions options = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(value);
        MessageCodec codec = GetOrCreateCodec(typeof(T));
        if (options.SelfDescribing)
        {
            SchemaMeta? meta = codec.BuildSchemaMeta(depth: 1, active: null, out bool cyclic);
            if (cyclic)
            {
                meta = codec.BuildValueSchemaMeta(value, depth: 1, active: null);
            }

            if (meta is not null)
            {
                SchemaMeta.WriteField0(encoder, meta, options.Deterministic);
            }
        }

        codec.EncodeObject(encoder, value, options.Deterministic, depth: 0);
    }

    public static T Unmarshal<T>(ReadOnlySpan<byte> data)
    {
        MessageCodec codec = GetOrCreateCodec(typeof(T));
        object instance = codec.DecodeNew(data, depth: 0, maxDepth: OmniBufConstants.MaxNestingDepth);
        return (T)instance;
    }

    public static void Unmarshal<T>(ReadOnlySpan<byte> data, T target)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(target);
        MessageCodec codec = GetOrCreateCodec(typeof(T));
        codec.DecodeInto(data, target, depth: 0, maxDepth: OmniBufConstants.MaxNestingDepth, merge: false);
    }

    private static MessageCodec GetOrCreateCodec(Type type)
    {
        if (Codecs.TryGetValue(type, out MessageCodec? existing) && existing.IsInitialized)
        {
            return existing;
        }

        lock (InitLock)
        {
            if (Codecs.TryGetValue(type, out existing) && existing.IsInitialized)
            {
                return existing;
            }

            if (existing is null)
            {
                existing = new MessageCodec(type);
                Codecs[type] = existing;
            }

            try
            {
                existing.Initialize(GetOrCreateStub);
                return existing;
            }
            catch
            {
                Codecs.TryRemove(type, out _);
                throw;
            }
        }
    }

    private static MessageCodec GetOrCreateStub(Type type)
    {
        if (!Codecs.TryGetValue(type, out MessageCodec? stub))
        {
            stub = new MessageCodec(type);
            Codecs[type] = stub;
        }

        if (!stub.IsInitialized && !stub.IsInitializing)
        {
            try
            {
                stub.Initialize(GetOrCreateStub);
            }
            catch
            {
                Codecs.TryRemove(type, out _);
                throw;
            }
        }

        return stub;
    }

    private static bool IsDisallowedMessageType(Type t)
    {
        if (t.IsPrimitive
            || t.IsEnum
            || t.IsInterface
            || t.IsArray
            || t.IsAbstract
            || t.IsPointer
            || t.IsByRef
            || t == typeof(string)
            || t == typeof(object)
            || t == typeof(decimal)
            || t == typeof(Guid)
            || t == typeof(DateTime)
            || t == typeof(DateTimeOffset)
            || t == typeof(TimeSpan)
            || t == typeof(DateOnly)
            || t == typeof(TimeOnly)
            || t == typeof(Half)
            || t == typeof(Int128)
            || t == typeof(UInt128)
            || t == typeof(Uri)
            || typeof(IEnumerable).IsAssignableFrom(t)
            || typeof(Delegate).IsAssignableFrom(t))
        {
            return true;
        }

        if (Nullable.GetUnderlyingType(t) is not null)
        {
            return true;
        }

        string? ns = t.Namespace;
        if (ns is not null && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private enum FieldCategory
    {
        Scalar,
        NullableScalar,
        SubMsg,
        Array,
        List,
        Map,
        NestedContainer,
    }

    private enum ScalarKind
    {
        None,
        Bool,
        Byte,
        UInt16,
        UInt32,
        UInt64,
        SByte,
        Int16,
        Int32,
        Int64,
        Float32,
        Float64,
        String,
        Bytes,
    }

    private enum ValueNodeKind
    {
        Scalar,
        SubMsg,
        List,
        Array,
        Map,
    }

    private sealed class ValueNode
    {
        public required ValueNodeKind Kind { get; init; }
        public required OmniBufWireType WireType { get; init; }
        public required Type ClrType { get; init; }
        public ScalarKind Scalar { get; init; }
        public Type? EnumType { get; init; }
        public MessageCodec? SubCodec { get; init; }
        public ValueNode? ElemNode { get; init; }
        public Func<IList>? ListFactory { get; init; }
        public ScalarKind MapKeyScalar { get; init; }
        public OmniBufWireType MapKeyWireType { get; init; }
        public Type? MapKeyEnumType { get; init; }
        public ValueNode? MapValNode { get; init; }
        public Func<IDictionary>? MapFactory { get; init; }
    }

    private delegate void FastInPlaceDecodeDelegate(
        ReadOnlySpan<byte> data,
        ref int pos,
        OmniBufWireType wt,
        object target,
        bool alreadySeen,
        int depth,
        int maxDepth,
        ref bool markedSeen);

    private sealed class FieldPlan
    {
        public required int Ordinal { get; init; }
        public required uint FieldNum { get; init; }
        public required string SchemaName { get; init; }
        public required PropertyInfo Property { get; init; }
        public required Type PropertyType { get; init; }
        public required FieldCategory Category { get; init; }
        public required OmniBufWireType WireType { get; init; }
        public required bool OmitEmpty { get; init; }
        public required Func<object, object?> Getter { get; init; }
        public Action<object, object?>? Setter { get; init; }
        public ScalarKind Scalar { get; init; }
        public Type? EnumType { get; init; }
        public bool IsNullableValueType { get; init; }
        public OmniBufWireType ElemWireType { get; init; }
        public ScalarKind ElemScalar { get; init; }
        public Type? ElemEnumType { get; init; }
        public Type? ElemType { get; init; }
        public MessageCodec? SubCodec { get; init; }
        public OmniBufWireType MapKeyWireType { get; init; }
        public ScalarKind MapKeyScalar { get; init; }
        public Type? MapKeyEnumType { get; init; }
        public Type? MapKeyType { get; init; }
        public OmniBufWireType MapValWireType { get; init; }
        public ScalarKind MapValScalar { get; init; }
        public Type? MapValEnumType { get; init; }
        public Type? MapValType { get; init; }
        public Func<IList>? ListFactory { get; init; }
        public Func<IDictionary>? MapFactory { get; init; }
        public ValueNode? ContainerNode { get; init; }
        public Action<OmniBufEncoder, object, bool, int>? FastEncoder { get; set; }
        public FastInPlaceDecodeDelegate? FastDecoder { get; set; }
        public Action<object>? FastResetter { get; set; }
    }

    private sealed class MessageCodec
    {
        private readonly Type _type;
        private string _typeName = string.Empty;
        private FieldPlan[] _fields = Array.Empty<FieldPlan>();
        private FieldPlan?[] _lowLookup = Array.Empty<FieldPlan?>();
        private Dictionary<uint, FieldPlan>? _overflowLookup;
        private Func<object>? _defaultCtor;
        private Func<object?[], object>? _paramCtor;
        private int[]? _ctorParamFieldOrdinals;

        public MessageCodec(Type type)
        {
            _type = type;
        }

        public bool IsInitialized { get; private set; }
        public bool IsInitializing { get; private set; }
        public bool CanDecodeInPlace => !_type.IsValueType && _defaultCtor is not null;
        public object CreateBlank() => _defaultCtor!();

        public void Initialize(Func<Type, MessageCodec> resolveSubCodec)
        {
            if (IsInitialized)
            {
                return;
            }

            if (IsDisallowedMessageType(_type))
            {
                throw new OmniBufException($"omnibuf: type {_type.FullName} cannot be serialized as an OmniBuf message");
            }

            IsInitializing = true;
            try
            {
                var msgAttr = _type.GetCustomAttribute<OmniBufMessageAttribute>();
                _typeName = !string.IsNullOrEmpty(msgAttr?.TypeName) ? msgAttr.TypeName! : _type.Name;

                PropertyInfo[] props = _type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
                ConstructorInfo? primaryCtor = SelectConstructor(_type, props, out Dictionary<string, OmniBufFieldAttribute>? ctorParamAttrs);

                var plans = new List<FieldPlan>();
                var seenNums = new HashSet<uint>();

                foreach (PropertyInfo prop in props)
                {
                    OmniBufFieldAttribute? attr = prop.GetCustomAttribute<OmniBufFieldAttribute>();
                    if (attr is null && ctorParamAttrs is not null)
                    {
                        ctorParamAttrs.TryGetValue(prop.Name, out attr);
                    }

                    if (attr is null)
                    {
                        continue;
                    }

                    if (!OmniBufConstants.IsUserFieldNumberValid(attr.FieldNumber))
                    {
                        throw new OmniBufException($"omnibuf: invalid field number {attr.FieldNumber} on {_type.Name}.{prop.Name}");
                    }

                    if (!seenNums.Add(attr.FieldNumber))
                    {
                        throw new OmniBufException($"omnibuf: duplicate field number {attr.FieldNumber} on {_type.Name}");
                    }

                    string schemaName = !string.IsNullOrEmpty(attr.Name) ? attr.Name! : ToSnakeCase(prop.Name);
                    FieldPlan plan = BuildFieldPlan(plans.Count, prop, attr, schemaName, resolveSubCodec);
                    plans.Add(plan);
                }

                if (plans.Count == 0 && msgAttr is null)
                {
                    throw new OmniBufException(
                        $"omnibuf: type {_type.FullName} has no [OmniBufField] properties (apply [OmniBufMessage] for an intentional empty message)");
                }

                plans.Sort((a, b) => a.FieldNum.CompareTo(b.FieldNum));
                for (int i = 0; i < plans.Count; i++)
                {
                    FieldPlan p = plans[i];
                    plans[i] = new FieldPlan
                    {
                        Ordinal = i,
                        FieldNum = p.FieldNum,
                        SchemaName = p.SchemaName,
                        Property = p.Property,
                        PropertyType = p.PropertyType,
                        Category = p.Category,
                        WireType = p.WireType,
                        OmitEmpty = p.OmitEmpty,
                        Getter = p.Getter,
                        Setter = p.Setter,
                        Scalar = p.Scalar,
                        EnumType = p.EnumType,
                        IsNullableValueType = p.IsNullableValueType,
                        ElemWireType = p.ElemWireType,
                        ElemScalar = p.ElemScalar,
                        ElemEnumType = p.ElemEnumType,
                        ElemType = p.ElemType,
                        SubCodec = p.SubCodec,
                        MapKeyWireType = p.MapKeyWireType,
                        MapKeyScalar = p.MapKeyScalar,
                        MapKeyEnumType = p.MapKeyEnumType,
                        MapKeyType = p.MapKeyType,
                        MapValWireType = p.MapValWireType,
                        MapValScalar = p.MapValScalar,
                        MapValEnumType = p.MapValEnumType,
                        MapValType = p.MapValType,
                        ListFactory = p.ListFactory,
                        MapFactory = p.MapFactory,
                        ContainerNode = p.ContainerNode,
                    };
                }

                _fields = plans.ToArray();
                _lowLookup = new FieldPlan?[64];
                foreach (FieldPlan plan in _fields)
                {
                    plan.FastEncoder = BuildFastFieldEncoder(plan);
                    if (!_type.IsValueType && plan.Setter is not null)
                    {
                        plan.FastDecoder = BuildFastInPlaceDecoder(plan);
                        plan.FastResetter = BuildFastResetter(plan);
                    }

                    if (plan.FieldNum < 64)
                    {
                        _lowLookup[plan.FieldNum] = plan;
                    }
                    else
                    {
                        _overflowLookup ??= new Dictionary<uint, FieldPlan>();
                        _overflowLookup[plan.FieldNum] = plan;
                    }
                }

                ConstructorInfo? parameterless = _type.GetConstructor(BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
                if (parameterless is not null && _fields.All(f => f.Setter is not null))
                {
                    NewExpression newExpr = Expression.New(parameterless);
                    UnaryExpression boxed = Expression.Convert(newExpr, typeof(object));
                    _defaultCtor = Expression.Lambda<Func<object>>(boxed).Compile();
                }
                else if (_type.IsValueType && _fields.All(f => f.Setter is not null))
                {
                    _defaultCtor = () => Activator.CreateInstance(_type)!;
                }
                else if (primaryCtor is not null)
                {
                    ParameterInfo[] cParams = primaryCtor.GetParameters();
                    _ctorParamFieldOrdinals = new int[cParams.Length];
                    for (int i = 0; i < cParams.Length; i++)
                    {
                        string pName = cParams[i].Name ?? string.Empty;
                        int idx = Array.FindIndex(_fields, f => f.PropertyType == cParams[i].ParameterType
                            && string.Equals(FindPropName(_type, f.FieldNum, ctorParamAttrs), pName, StringComparison.OrdinalIgnoreCase));
                        _ctorParamFieldOrdinals[i] = idx;
                    }

                    ParameterExpression arrParam = Expression.Parameter(typeof(object?[]), "args");
                    var callArgs = new Expression[cParams.Length];
                    for (int i = 0; i < cParams.Length; i++)
                    {
                        BinaryExpression elem = Expression.ArrayIndex(arrParam, Expression.Constant(i));
                        callArgs[i] = Expression.Convert(elem, cParams[i].ParameterType);
                    }

                    UnaryExpression body = Expression.Convert(Expression.New(primaryCtor, callArgs), typeof(object));
                    _paramCtor = Expression.Lambda<Func<object?[], object>>(body, arrParam).Compile();
                }
                else
                {
                    throw new OmniBufException($"omnibuf: type {_type.Name} requires either writable properties with a parameterless constructor or a matching record constructor");
                }

                IsInitialized = true;
            }
            finally
            {
                IsInitializing = false;
            }
        }

        private static string FindPropName(Type t, uint fnum, Dictionary<string, OmniBufFieldAttribute>? ctorAttrs)
        {
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                OmniBufFieldAttribute? a = p.GetCustomAttribute<OmniBufFieldAttribute>();
                if (a is null && ctorAttrs is not null)
                {
                    ctorAttrs.TryGetValue(p.Name, out a);
                }

                if (a is not null && a.FieldNumber == fnum)
                {
                    return p.Name;
                }
            }

            return string.Empty;
        }

        private static ConstructorInfo? SelectConstructor(
            Type type,
            PropertyInfo[] props,
            out Dictionary<string, OmniBufFieldAttribute>? ctorParamAttrs)
        {
            ctorParamAttrs = null;
            ConstructorInfo[] ctors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
            ConstructorInfo? best = null;
            foreach (ConstructorInfo ctor in ctors)
            {
                ParameterInfo[] ps = ctor.GetParameters();
                if (ps.Length == 0)
                {
                    continue;
                }

                if (best is null || ps.Length > best.GetParameters().Length)
                {
                    best = ctor;
                }
            }

            if (best is not null)
            {
                var map = new Dictionary<string, OmniBufFieldAttribute>(StringComparer.OrdinalIgnoreCase);
                foreach (ParameterInfo p in best.GetParameters())
                {
                    var attr = p.GetCustomAttribute<OmniBufFieldAttribute>();
                    if (attr is not null && p.Name is not null)
                    {
                        PropertyInfo? match = Array.Find(props, pr => string.Equals(pr.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                        if (match is not null)
                        {
                            map[match.Name] = attr;
                        }
                    }
                }

                if (map.Count > 0)
                {
                    ctorParamAttrs = map;
                }
            }

            return best;
        }

        public SchemaMeta? BuildSchemaMeta(int depth, HashSet<Type>? active, out bool cyclic)
        {
            cyclic = false;
            if (depth + 1 > OmniBufConstants.MaxNestingDepth)
            {
                return null;
            }

            active ??= new HashSet<Type>();
            if (!active.Add(_type))
            {
                cyclic = true;
                return null;
            }

            try
            {
                var meta = new SchemaMeta
                {
                    TypeName = _typeName,
                    Fields = new Dictionary<uint, string>(_fields.Length),
                };

                foreach (FieldPlan f in _fields)
                {
                    meta.Fields[f.FieldNum] = f.SchemaName;
                    MessageCodec? childCodec = f.SubCodec ?? FindLeafSubCodec(f.ContainerNode);
                    if (childCodec is not null)
                    {
                        SchemaMeta? childMeta = childCodec.BuildSchemaMeta(depth + 2, active, out bool childCyclic);
                        cyclic |= childCyclic;
                        if (childMeta is not null)
                        {
                            meta.Children[f.FieldNum] = childMeta;
                        }
                    }
                }

                return meta;
            }
            finally
            {
                active.Remove(_type);
            }
        }

        public SchemaMeta? BuildValueSchemaMeta(object? instance, int depth, HashSet<object>? active)
        {
            if (instance is null || depth + 1 > OmniBufConstants.MaxNestingDepth)
            {
                return null;
            }

            bool trackedRef = !_type.IsValueType;
            if (trackedRef)
            {
                active ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
                if (!active.Add(instance))
                {
                    return null;
                }
            }

            try
            {
                var meta = new SchemaMeta
                {
                    TypeName = _typeName,
                    Fields = new Dictionary<uint, string>(_fields.Length),
                };

                foreach (FieldPlan f in _fields)
                {
                    meta.Fields[f.FieldNum] = f.SchemaName;
                    if (depth + 3 > OmniBufConstants.MaxNestingDepth)
                    {
                        continue;
                    }

                    MessageCodec? childCodec = f.SubCodec ?? FindLeafSubCodec(f.ContainerNode);
                    if (childCodec is null)
                    {
                        continue;
                    }

                    object? fieldVal = f.Getter(instance);
                    if (fieldVal is null)
                    {
                        continue;
                    }

                    SchemaMeta? childMeta = f.Category switch
                    {
                        FieldCategory.SubMsg => childCodec.BuildValueSchemaMeta(fieldVal, depth + 2, active),
                        FieldCategory.Array or FieldCategory.List => CollectListSchemaMeta((IEnumerable)fieldVal, childCodec, depth + 2, active),
                        FieldCategory.Map => CollectDictSchemaMeta((IDictionary)fieldVal, childCodec, depth + 2, active),
                        FieldCategory.NestedContainer => CollectNestedContainerSchemaMeta(fieldVal, f.ContainerNode!, childCodec, depth + 2, active),
                        _ => null,
                    };

                    if (childMeta is not null)
                    {
                        meta.Children[f.FieldNum] = childMeta;
                    }
                }

                return meta;
            }
            finally
            {
                if (trackedRef && active is not null)
                {
                    active.Remove(instance);
                }
            }
        }

        private static SchemaMeta? CollectListSchemaMeta(IEnumerable list, MessageCodec leafCodec, int childDepth, HashSet<object>? active)
        {
            SchemaMeta? merged = null;
            foreach (object? item in list)
            {
                if (item is not null)
                {
                    merged = SchemaMeta.Merge(merged, leafCodec.BuildValueSchemaMeta(item, childDepth, active));
                }
            }

            return merged;
        }

        private static SchemaMeta? CollectDictSchemaMeta(IDictionary dict, MessageCodec leafCodec, int childDepth, HashSet<object>? active)
        {
            SchemaMeta? merged = null;
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Value is not null)
                {
                    merged = SchemaMeta.Merge(merged, leafCodec.BuildValueSchemaMeta(entry.Value, childDepth, active));
                }
            }

            return merged;
        }

        private static SchemaMeta? CollectNestedContainerSchemaMeta(object val, ValueNode node, MessageCodec leafCodec, int childDepth, HashSet<object>? active)
        {
            if (node.Kind == ValueNodeKind.SubMsg)
            {
                return leafCodec.BuildValueSchemaMeta(val, childDepth, active);
            }

            SchemaMeta? merged = null;
            if (node.Kind is ValueNodeKind.Array or ValueNodeKind.List && val is IEnumerable enumerable)
            {
                foreach (object? item in enumerable)
                {
                    if (item is not null)
                    {
                        merged = SchemaMeta.Merge(merged, CollectNestedContainerSchemaMeta(item, node.ElemNode!, leafCodec, childDepth, active));
                    }
                }
            }
            else if (node.Kind == ValueNodeKind.Map && val is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Value is not null)
                    {
                        merged = SchemaMeta.Merge(merged, CollectNestedContainerSchemaMeta(entry.Value, node.MapValNode!, leafCodec, childDepth, active));
                    }
                }
            }

            return merged;
        }

        public void EncodeObject(OmniBufEncoder encoder, object instance, bool deterministic, int depth)
        {
            if (depth > OmniBufConstants.MaxNestingDepth)
            {
                throw new OmniBufException($"omnibuf: encode nesting depth {depth} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
            }

            for (int i = 0; i < _fields.Length; i++)
            {
                _fields[i].FastEncoder!(encoder, instance, deterministic, depth);
            }
        }

        public object DecodeNew(ReadOnlySpan<byte> data, int depth, int maxDepth)
        {
            if (_defaultCtor is not null)
            {
                object instance = _defaultCtor();
                DecodeInto(data, instance, depth, maxDepth, merge: false);
                return instance;
            }

            return DecodeViaValues(data, existing: null, depth, maxDepth);
        }

        public object DecodeMerge(ReadOnlySpan<byte> data, object existing, int depth, int maxDepth)
        {
            if (_defaultCtor is not null && !_type.IsValueType)
            {
                DecodeInto(data, existing, depth, maxDepth, merge: true);
                return existing;
            }

            if (_defaultCtor is not null && _type.IsValueType)
            {
                object copy = existing;
                DecodeInto(data, copy, depth, maxDepth, merge: true);
                return copy;
            }

            return DecodeViaValues(data, existing, depth, maxDepth);
        }

        private object DecodeViaValues(ReadOnlySpan<byte> data, object? existing, int depth, int maxDepth)
        {
            if (depth > maxDepth)
            {
                throw new OmniBufException($"omnibuf: nesting depth {depth} exceeds maximum {maxDepth}");
            }

            bool merge = existing is not null;
            var values = new object?[_fields.Length];
            var seen = new bool[_fields.Length];
            for (int i = 0; i < _fields.Length; i++)
            {
                values[i] = existing is not null ? _fields[i].Getter(existing) : CreateDefaultFieldValue(_fields[i]);
            }

            int pos = 0;
            while (pos < data.Length)
            {
                (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
                if (fnum == 0)
                {
                    SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
                    continue;
                }

                FieldPlan? plan = LookupField(fnum);
                if (plan is null)
                {
                    OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                    continue;
                }

                bool alreadySeen = seen[plan.Ordinal] || merge;
                values[plan.Ordinal] = DecodeFieldValue(data, ref pos, wt, plan, values[plan.Ordinal], alreadySeen, depth, maxDepth, ref seen[plan.Ordinal]);
            }

            var ctorArgs = new object?[_ctorParamFieldOrdinals!.Length];
            for (int i = 0; i < _ctorParamFieldOrdinals.Length; i++)
            {
                int ord = _ctorParamFieldOrdinals[i];
                ctorArgs[i] = ord >= 0 ? values[ord] : null;
            }

            return _paramCtor!(ctorArgs);
        }

        public void DecodeInto(ReadOnlySpan<byte> data, object target, int depth, int maxDepth, bool merge)
        {
            if (depth > maxDepth)
            {
                throw new OmniBufException($"omnibuf: nesting depth {depth} exceeds maximum {maxDepth}");
            }

            if (_fields.Length <= 64)
            {
                ulong seenLow = 0;
                ulong expectedMask = _fields.Length == 64 ? ulong.MaxValue : ((1UL << _fields.Length) - 1UL);
                int pos = 0;
                while (pos < data.Length)
                {
                    (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref pos);
                    if (fnum == 0)
                    {
                        SchemaMeta.ValidateField0(data, ref pos, wt, depth, maxDepth);
                        continue;
                    }

                    FieldPlan? plan = LookupField(fnum);
                    if (plan is null)
                    {
                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                        continue;
                    }

                    ulong bit = 1UL << plan.Ordinal;
                    bool alreadySeen = (seenLow & bit) != 0 || merge;
                    bool marked = (seenLow & bit) != 0;
                    if (plan.FastDecoder is not null)
                    {
                        plan.FastDecoder(data, ref pos, wt, target, alreadySeen, depth, maxDepth, ref marked);
                    }
                    else
                    {
                        object? currentVal = plan.Getter(target);
                        object? nextVal = DecodeFieldValue(data, ref pos, wt, plan, currentVal, alreadySeen, depth, maxDepth, ref marked);
                        plan.Setter!(target, nextVal);
                    }

                    if (marked)
                    {
                        seenLow |= bit;
                    }
                }

                if (!merge && seenLow != expectedMask)
                {
                    for (int i = 0; i < _fields.Length; i++)
                    {
                        if ((seenLow & (1UL << i)) == 0)
                        {
                            FieldPlan plan = _fields[i];
                            if (plan.FastResetter is not null)
                            {
                                plan.FastResetter(target);
                            }
                            else
                            {
                                plan.Setter!(target, CreateDefaultFieldValue(plan));
                            }
                        }
                    }
                }

                return;
            }

            var seen = new bool[_fields.Length];
            int p2 = 0;
            while (p2 < data.Length)
            {
                (uint fnum, OmniBufWireType wt) = OmniBufDecoder.ReadTag(data, ref p2);
                if (fnum == 0)
                {
                    SchemaMeta.ValidateField0(data, ref p2, wt, depth, maxDepth);
                    continue;
                }

                FieldPlan? plan = LookupField(fnum);
                if (plan is null)
                {
                    OmniBufDecoder.ValidateValue(data, ref p2, wt, depth, maxDepth);
                    continue;
                }

                bool alreadySeen = seen[plan.Ordinal] || merge;
                object? currentVal = plan.Getter(target);
                object? nextVal = DecodeFieldValue(data, ref p2, wt, plan, currentVal, alreadySeen, depth, maxDepth, ref seen[plan.Ordinal]);
                plan.Setter!(target, nextVal);
            }

            if (!merge)
            {
                for (int i = 0; i < _fields.Length; i++)
                {
                    if (!seen[i])
                    {
                        FieldPlan plan = _fields[i];
                        plan.Setter!(target, CreateDefaultFieldValue(plan));
                    }
                }
            }
        }

        private FieldPlan? LookupField(uint fnum)
        {
            if (fnum < 64)
            {
                return _lowLookup[fnum];
            }

            if (_overflowLookup is not null && _overflowLookup.TryGetValue(fnum, out FieldPlan? plan))
            {
                return plan;
            }

            return null;
        }
    }

    private static MessageCodec? FindLeafSubCodec(ValueNode? node)
    {
        while (node is not null)
        {
            if (node.Kind == ValueNodeKind.SubMsg)
            {
                return node.SubCodec;
            }

            node = node.ElemNode ?? node.MapValNode;
        }

        return null;
    }

    private static FieldPlan BuildFieldPlan(
        int ordinal,
        PropertyInfo prop,
        OmniBufFieldAttribute attr,
        string schemaName,
        Func<Type, MessageCodec> resolveSubCodec)
    {
        Type propType = prop.PropertyType;
        Func<object, object?> getter = CompileGetter(prop);
        Action<object, object?>? setter = CompileSetter(prop);

        if (Nullable.GetUnderlyingType(propType) is { } nullableInner)
        {
            if (TryClassifyScalar(nullableInner, attr.HasExplicitWire ? attr.Wire : null, out ScalarKind nScalar, out OmniBufWireType nWt, out Type? nEnumType))
            {
                return new FieldPlan
                {
                    Ordinal = ordinal,
                    FieldNum = attr.FieldNumber,
                    SchemaName = schemaName,
                    Property = prop,
                    PropertyType = propType,
                    Category = FieldCategory.NullableScalar,
                    WireType = nWt,
                    OmitEmpty = false,
                    Getter = getter,
                    Setter = setter,
                    Scalar = nScalar,
                    EnumType = nEnumType,
                    IsNullableValueType = true,
                };
            }

            if (attr.HasExplicitWire && attr.Wire != OmniBufWireType.SubMsg)
            {
                throw new OmniBufException($"omnibuf: invalid Wire override {attr.Wire} on SubMsg property {prop.DeclaringType?.Name}.{prop.Name}");
            }

            return new FieldPlan
            {
                Ordinal = ordinal,
                FieldNum = attr.FieldNumber,
                SchemaName = schemaName,
                Property = prop,
                PropertyType = propType,
                Category = FieldCategory.SubMsg,
                WireType = OmniBufWireType.SubMsg,
                OmitEmpty = attr.OmitEmpty,
                Getter = getter,
                Setter = setter,
                IsNullableValueType = true,
                SubCodec = resolveSubCodec(nullableInner),
            };
        }

        if (TryClassifyScalar(propType, attr.HasExplicitWire ? attr.Wire : null, out ScalarKind scalar, out OmniBufWireType scalarWt, out Type? enumType))
        {
            return new FieldPlan
            {
                Ordinal = ordinal,
                FieldNum = attr.FieldNumber,
                SchemaName = schemaName,
                Property = prop,
                PropertyType = propType,
                Category = FieldCategory.Scalar,
                WireType = scalarWt,
                OmitEmpty = attr.OmitEmpty,
                Getter = getter,
                Setter = setter,
                Scalar = scalar,
                EnumType = enumType,
            };
        }

        if (propType.IsArray && propType != typeof(byte[]))
        {
            if (propType.GetArrayRank() != 1)
            {
                throw new OmniBufException($"omnibuf: multi-dimensional array {prop.DeclaringType?.Name}.{prop.Name} is not supported");
            }

            Type elemType = propType.GetElementType()!;
            OmniBufWireType? explicitElemWt = attr.HasExplicitValueWire ? attr.ValueWire : (attr.HasExplicitWire && attr.Wire != OmniBufWireType.Array ? attr.Wire : null);
            if (TryClassifyScalar(elemType, explicitElemWt, out ScalarKind elemScalar, out OmniBufWireType elemWt, out Type? elemEnumType))
            {
                return new FieldPlan
                {
                    Ordinal = ordinal,
                    FieldNum = attr.FieldNumber,
                    SchemaName = schemaName,
                    Property = prop,
                    PropertyType = propType,
                    Category = FieldCategory.Array,
                    WireType = OmniBufWireType.Array,
                    OmitEmpty = attr.OmitEmpty,
                    Getter = getter,
                    Setter = setter,
                    ElemWireType = elemWt,
                    ElemScalar = elemScalar,
                    ElemEnumType = elemEnumType,
                    ElemType = elemType,
                };
            }

            if (explicitElemWt is not null && explicitElemWt != OmniBufWireType.SubMsg && explicitElemWt != OmniBufWireType.Array && explicitElemWt != OmniBufWireType.Map)
            {
                throw new OmniBufException($"omnibuf: invalid element Wire override {explicitElemWt} on {prop.DeclaringType?.Name}.{prop.Name}");
            }

            if (IsContainerClrType(elemType))
            {
                ValueNode containerNode = BuildValueNode(propType, null, explicitElemWt, resolveSubCodec);
                return new FieldPlan
                {
                    Ordinal = ordinal,
                    FieldNum = attr.FieldNumber,
                    SchemaName = schemaName,
                    Property = prop,
                    PropertyType = propType,
                    Category = FieldCategory.NestedContainer,
                    WireType = OmniBufWireType.Array,
                    OmitEmpty = attr.OmitEmpty,
                    Getter = getter,
                    Setter = setter,
                    ElemType = elemType,
                    ContainerNode = containerNode,
                };
            }

            MessageCodec subCodec = resolveSubCodec(elemType);
            return new FieldPlan
            {
                Ordinal = ordinal,
                FieldNum = attr.FieldNumber,
                SchemaName = schemaName,
                Property = prop,
                PropertyType = propType,
                Category = FieldCategory.Array,
                WireType = OmniBufWireType.Array,
                OmitEmpty = attr.OmitEmpty,
                Getter = getter,
                Setter = setter,
                ElemWireType = OmniBufWireType.SubMsg,
                ElemType = elemType,
                SubCodec = subCodec,
            };
        }

        if (propType.IsGenericType)
        {
            Type genDef = propType.GetGenericTypeDefinition();
            if (genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(IReadOnlyList<>))
            {
                Type elemType = propType.GetGenericArguments()[0];
                Type concreteListType = typeof(List<>).MakeGenericType(elemType);
                Func<IList> listFactory = Expression.Lambda<Func<IList>>(Expression.Convert(Expression.New(concreteListType), typeof(IList))).Compile();
                OmniBufWireType? explicitElemWt = attr.HasExplicitValueWire ? attr.ValueWire : (attr.HasExplicitWire && attr.Wire != OmniBufWireType.Array ? attr.Wire : null);

                if (TryClassifyScalar(elemType, explicitElemWt, out ScalarKind elemScalar, out OmniBufWireType elemWt, out Type? elemEnumType))
                {
                    return new FieldPlan
                    {
                        Ordinal = ordinal,
                        FieldNum = attr.FieldNumber,
                        SchemaName = schemaName,
                        Property = prop,
                        PropertyType = propType,
                        Category = FieldCategory.List,
                        WireType = OmniBufWireType.Array,
                        OmitEmpty = attr.OmitEmpty,
                        Getter = getter,
                        Setter = setter,
                        ElemWireType = elemWt,
                        ElemScalar = elemScalar,
                        ElemEnumType = elemEnumType,
                        ElemType = elemType,
                        ListFactory = listFactory,
                    };
                }

                if (explicitElemWt is not null && explicitElemWt != OmniBufWireType.SubMsg && explicitElemWt != OmniBufWireType.Array && explicitElemWt != OmniBufWireType.Map)
                {
                    throw new OmniBufException($"omnibuf: invalid element Wire override {explicitElemWt} on {prop.DeclaringType?.Name}.{prop.Name}");
                }

                if (IsContainerClrType(elemType))
                {
                    ValueNode containerNode = BuildValueNode(propType, null, explicitElemWt, resolveSubCodec);
                    return new FieldPlan
                    {
                        Ordinal = ordinal,
                        FieldNum = attr.FieldNumber,
                        SchemaName = schemaName,
                        Property = prop,
                        PropertyType = propType,
                        Category = FieldCategory.NestedContainer,
                        WireType = OmniBufWireType.Array,
                        OmitEmpty = attr.OmitEmpty,
                        Getter = getter,
                        Setter = setter,
                        ElemType = elemType,
                        ListFactory = listFactory,
                        ContainerNode = containerNode,
                    };
                }

                MessageCodec subCodec = resolveSubCodec(elemType);
                return new FieldPlan
                {
                    Ordinal = ordinal,
                    FieldNum = attr.FieldNumber,
                    SchemaName = schemaName,
                    Property = prop,
                    PropertyType = propType,
                    Category = FieldCategory.List,
                    WireType = OmniBufWireType.Array,
                    OmitEmpty = attr.OmitEmpty,
                    Getter = getter,
                    Setter = setter,
                    ElemWireType = OmniBufWireType.SubMsg,
                    ElemType = elemType,
                    SubCodec = subCodec,
                    ListFactory = listFactory,
                };
            }

            if (genDef == typeof(Dictionary<,>) || genDef == typeof(IDictionary<,>) || genDef == typeof(IReadOnlyDictionary<,>))
            {
                if (attr.HasExplicitWire && attr.Wire != OmniBufWireType.Map)
                {
                    throw new OmniBufException($"omnibuf: invalid Wire override {attr.Wire} on map property {prop.DeclaringType?.Name}.{prop.Name}");
                }

                Type[] args = propType.GetGenericArguments();
                Type keyType = args[0];
                Type valType = args[1];
                Type concreteMapType = typeof(Dictionary<,>).MakeGenericType(keyType, valType);
                Func<IDictionary> mapFactory = Expression.Lambda<Func<IDictionary>>(Expression.Convert(Expression.New(concreteMapType), typeof(IDictionary))).Compile();

                if (!TryClassifyScalar(keyType, attr.HasExplicitKeyWire ? attr.KeyWire : null, out ScalarKind keyScalar, out OmniBufWireType keyWt, out Type? keyEnumType)
                    || !OmniBufConstants.IsValidMapKeyType(keyWt))
                {
                    throw new OmniBufException($"omnibuf: unsupported map key type {keyType.Name} on {prop.Name}");
                }

                OmniBufWireType? explicitValWt = attr.HasExplicitValueWire ? attr.ValueWire : null;
                if (TryClassifyScalar(valType, explicitValWt, out ScalarKind valScalar, out OmniBufWireType valWt, out Type? valEnumType))
                {
                    return new FieldPlan
                    {
                        Ordinal = ordinal,
                        FieldNum = attr.FieldNumber,
                        SchemaName = schemaName,
                        Property = prop,
                        PropertyType = propType,
                        Category = FieldCategory.Map,
                        WireType = OmniBufWireType.Map,
                        OmitEmpty = attr.OmitEmpty,
                        Getter = getter,
                        Setter = setter,
                        MapKeyWireType = keyWt,
                        MapKeyScalar = keyScalar,
                        MapKeyEnumType = keyEnumType,
                        MapKeyType = keyType,
                        MapValWireType = valWt,
                        MapValScalar = valScalar,
                        MapValEnumType = valEnumType,
                        MapValType = valType,
                        MapFactory = mapFactory,
                    };
                }

                if (explicitValWt is not null && explicitValWt != OmniBufWireType.SubMsg && explicitValWt != OmniBufWireType.Array && explicitValWt != OmniBufWireType.Map)
                {
                    throw new OmniBufException($"omnibuf: invalid ValueWire override {explicitValWt} on {prop.DeclaringType?.Name}.{prop.Name}");
                }

                if (IsContainerClrType(valType))
                {
                    ValueNode containerNode = BuildValueNode(propType, attr.HasExplicitKeyWire ? attr.KeyWire : null, explicitValWt, resolveSubCodec);
                    return new FieldPlan
                    {
                        Ordinal = ordinal,
                        FieldNum = attr.FieldNumber,
                        SchemaName = schemaName,
                        Property = prop,
                        PropertyType = propType,
                        Category = FieldCategory.NestedContainer,
                        WireType = OmniBufWireType.Map,
                        OmitEmpty = attr.OmitEmpty,
                        Getter = getter,
                        Setter = setter,
                        MapFactory = mapFactory,
                        ContainerNode = containerNode,
                    };
                }

                MessageCodec subCodec = resolveSubCodec(valType);
                return new FieldPlan
                {
                    Ordinal = ordinal,
                    FieldNum = attr.FieldNumber,
                    SchemaName = schemaName,
                    Property = prop,
                    PropertyType = propType,
                    Category = FieldCategory.Map,
                    WireType = OmniBufWireType.Map,
                    OmitEmpty = attr.OmitEmpty,
                    Getter = getter,
                    Setter = setter,
                    MapKeyWireType = keyWt,
                    MapKeyScalar = keyScalar,
                    MapKeyEnumType = keyEnumType,
                    MapKeyType = keyType,
                    MapValWireType = OmniBufWireType.SubMsg,
                    MapValType = valType,
                    SubCodec = subCodec,
                    MapFactory = mapFactory,
                };
            }
        }

        if (attr.HasExplicitWire && attr.Wire != OmniBufWireType.SubMsg)
        {
            throw new OmniBufException($"omnibuf: invalid Wire override {attr.Wire} on SubMsg property {prop.DeclaringType?.Name}.{prop.Name}");
        }

        return new FieldPlan
        {
            Ordinal = ordinal,
            FieldNum = attr.FieldNumber,
            SchemaName = schemaName,
            Property = prop,
            PropertyType = propType,
            Category = FieldCategory.SubMsg,
            WireType = OmniBufWireType.SubMsg,
            OmitEmpty = attr.OmitEmpty,
            Getter = getter,
            Setter = setter,
            SubCodec = resolveSubCodec(propType),
        };
    }

    private static bool IsContainerClrType(Type t)
    {
        if (t.IsArray && t != typeof(byte[]))
        {
            return true;
        }

        if (t.IsGenericType)
        {
            Type g = t.GetGenericTypeDefinition();
            return g == typeof(List<>) || g == typeof(IList<>) || g == typeof(IReadOnlyList<>)
                || g == typeof(Dictionary<,>) || g == typeof(IDictionary<,>) || g == typeof(IReadOnlyDictionary<,>);
        }

        return false;
    }

    private static ValueNode BuildValueNode(
        Type t,
        OmniBufWireType? keyExplicitWire,
        OmniBufWireType? valOrElemExplicitWire,
        Func<Type, MessageCodec> resolveSubCodec)
    {
        if (TryClassifyScalar(t, valOrElemExplicitWire, out ScalarKind scalar, out OmniBufWireType scalarWt, out Type? enumType))
        {
            return new ValueNode
            {
                Kind = ValueNodeKind.Scalar,
                WireType = scalarWt,
                ClrType = t,
                Scalar = scalar,
                EnumType = enumType,
            };
        }

        if (t.IsArray && t != typeof(byte[]))
        {
            if (t.GetArrayRank() != 1)
            {
                throw new OmniBufException($"omnibuf: multi-dimensional array {t.Name} is not supported");
            }

            Type elemType = t.GetElementType()!;
            OmniBufWireType? innerOverride = valOrElemExplicitWire is not null && valOrElemExplicitWire != OmniBufWireType.Array ? valOrElemExplicitWire : null;
            ValueNode elemNode = BuildValueNode(elemType, null, innerOverride, resolveSubCodec);
            return new ValueNode
            {
                Kind = ValueNodeKind.Array,
                WireType = OmniBufWireType.Array,
                ClrType = t,
                ElemNode = elemNode,
            };
        }

        if (t.IsGenericType)
        {
            Type g = t.GetGenericTypeDefinition();
            if (g == typeof(List<>) || g == typeof(IList<>) || g == typeof(IReadOnlyList<>))
            {
                Type elemType = t.GetGenericArguments()[0];
                Type concreteList = typeof(List<>).MakeGenericType(elemType);
                Func<IList> listFactory = Expression.Lambda<Func<IList>>(Expression.Convert(Expression.New(concreteList), typeof(IList))).Compile();
                OmniBufWireType? innerOverride = valOrElemExplicitWire is not null && valOrElemExplicitWire != OmniBufWireType.Array ? valOrElemExplicitWire : null;
                ValueNode elemNode = BuildValueNode(elemType, null, innerOverride, resolveSubCodec);
                return new ValueNode
                {
                    Kind = ValueNodeKind.List,
                    WireType = OmniBufWireType.Array,
                    ClrType = t,
                    ElemNode = elemNode,
                    ListFactory = listFactory,
                };
            }

            if (g == typeof(Dictionary<,>) || g == typeof(IDictionary<,>) || g == typeof(IReadOnlyDictionary<,>))
            {
                Type[] args = t.GetGenericArguments();
                Type keyType = args[0];
                Type valType = args[1];
                if (!TryClassifyScalar(keyType, keyExplicitWire, out ScalarKind keyScalar, out OmniBufWireType keyWt, out Type? keyEnumType)
                    || !OmniBufConstants.IsValidMapKeyType(keyWt))
                {
                    throw new OmniBufException($"omnibuf: unsupported map key type {keyType.Name}");
                }

                Type concreteMap = typeof(Dictionary<,>).MakeGenericType(keyType, valType);
                Func<IDictionary> mapFactory = Expression.Lambda<Func<IDictionary>>(Expression.Convert(Expression.New(concreteMap), typeof(IDictionary))).Compile();
                OmniBufWireType? innerValOverride = valOrElemExplicitWire is not null && valOrElemExplicitWire != OmniBufWireType.Map ? valOrElemExplicitWire : null;
                ValueNode valNode = BuildValueNode(valType, null, innerValOverride, resolveSubCodec);
                return new ValueNode
                {
                    Kind = ValueNodeKind.Map,
                    WireType = OmniBufWireType.Map,
                    ClrType = t,
                    MapKeyScalar = keyScalar,
                    MapKeyWireType = keyWt,
                    MapKeyEnumType = keyEnumType,
                    MapValNode = valNode,
                    MapFactory = mapFactory,
                };
            }
        }

        return new ValueNode
        {
            Kind = ValueNodeKind.SubMsg,
            WireType = OmniBufWireType.SubMsg,
            ClrType = t,
            SubCodec = resolveSubCodec(t),
        };
    }

    private static bool TryClassifyScalar(
        Type origType,
        OmniBufWireType? explicitWire,
        out ScalarKind kind,
        out OmniBufWireType wireType,
        out Type? enumType)
    {
        Type t = origType;
        if (t.IsEnum)
        {
            enumType = t;
            t = Enum.GetUnderlyingType(t);
        }
        else
        {
            enumType = null;
        }

        if (t == typeof(bool))
        {
            if (explicitWire is { } ew && ew is not (OmniBufWireType.BoolFalse or OmniBufWireType.BoolTrue))
            {
                throw new OmniBufException($"omnibuf: Wire override {ew} is not valid for bool");
            }

            kind = ScalarKind.Bool;
            wireType = OmniBufWireType.BoolFalse;
            return true;
        }

        if (t == typeof(byte))
        {
            OmniBufWireType wt = explicitWire ?? OmniBufWireType.Uint;
            if (wt is not (OmniBufWireType.Uint or OmniBufWireType.Sint))
            {
                throw new OmniBufException($"omnibuf: Wire override {wt} is not valid for {origType.Name}");
            }

            kind = ScalarKind.Byte;
            wireType = wt;
            return true;
        }

        if (t == typeof(ushort))
        {
            OmniBufWireType wt = explicitWire ?? OmniBufWireType.Uint;
            if (wt is not (OmniBufWireType.Uint or OmniBufWireType.Sint))
            {
                throw new OmniBufException($"omnibuf: Wire override {wt} is not valid for {origType.Name}");
            }

            kind = ScalarKind.UInt16;
            wireType = wt;
            return true;
        }

        if (t == typeof(uint))
        {
            OmniBufWireType wt = explicitWire ?? OmniBufWireType.Uint;
            if (wt is not (OmniBufWireType.Uint or OmniBufWireType.Sint or OmniBufWireType.Fixed32 or OmniBufWireType.Sfixed32))
            {
                throw new OmniBufException($"omnibuf: Wire override {wt} is not valid for {origType.Name}");
            }

            kind = ScalarKind.UInt32;
            wireType = wt;
            return true;
        }

        if (t == typeof(ulong))
        {
            OmniBufWireType wt = explicitWire ?? OmniBufWireType.Uint;
            if (wt is not (OmniBufWireType.Uint or OmniBufWireType.Sint or OmniBufWireType.Fixed64 or OmniBufWireType.Sfixed64))
            {
                throw new OmniBufException($"omnibuf: Wire override {wt} is not valid for {origType.Name}");
            }

            kind = ScalarKind.UInt64;
            wireType = wt;
            return true;
        }

        if (t == typeof(sbyte))
        {
            OmniBufWireType wt = explicitWire ?? OmniBufWireType.Sint;
            if (wt is not (OmniBufWireType.Sint or OmniBufWireType.Uint))
            {
                throw new OmniBufException($"omnibuf: Wire override {wt} is not valid for {origType.Name}");
            }

            kind = ScalarKind.SByte;
            wireType = wt;
            return true;
        }

        if (t == typeof(short))
        {
            OmniBufWireType wt = explicitWire ?? OmniBufWireType.Sint;
            if (wt is not (OmniBufWireType.Sint or OmniBufWireType.Uint))
            {
                throw new OmniBufException($"omnibuf: Wire override {wt} is not valid for {origType.Name}");
            }

            kind = ScalarKind.Int16;
            wireType = wt;
            return true;
        }

        if (t == typeof(int))
        {
            OmniBufWireType wt = explicitWire ?? OmniBufWireType.Sint;
            if (wt is not (OmniBufWireType.Sint or OmniBufWireType.Uint or OmniBufWireType.Sfixed32 or OmniBufWireType.Fixed32))
            {
                throw new OmniBufException($"omnibuf: Wire override {wt} is not valid for {origType.Name}");
            }

            kind = ScalarKind.Int32;
            wireType = wt;
            return true;
        }

        if (t == typeof(long))
        {
            OmniBufWireType wt = explicitWire ?? OmniBufWireType.Sint;
            if (wt is not (OmniBufWireType.Sint or OmniBufWireType.Uint or OmniBufWireType.Sfixed64 or OmniBufWireType.Fixed64))
            {
                throw new OmniBufException($"omnibuf: Wire override {wt} is not valid for {origType.Name}");
            }

            kind = ScalarKind.Int64;
            wireType = wt;
            return true;
        }

        if (t == typeof(float))
        {
            if (explicitWire is { } ew && ew != OmniBufWireType.Float32)
            {
                throw new OmniBufException($"omnibuf: Wire override {ew} is not valid for float");
            }

            kind = ScalarKind.Float32;
            wireType = OmniBufWireType.Float32;
            return true;
        }

        if (t == typeof(double))
        {
            if (explicitWire is { } ew && ew != OmniBufWireType.Float64)
            {
                throw new OmniBufException($"omnibuf: Wire override {ew} is not valid for double");
            }

            kind = ScalarKind.Float64;
            wireType = OmniBufWireType.Float64;
            return true;
        }

        if (t == typeof(string))
        {
            if (explicitWire is { } ew && ew != OmniBufWireType.String)
            {
                throw new OmniBufException($"omnibuf: Wire override {ew} is not valid for string");
            }

            kind = ScalarKind.String;
            wireType = OmniBufWireType.String;
            return true;
        }

        if (t == typeof(byte[]))
        {
            if (explicitWire is { } ew && ew != OmniBufWireType.Bytes)
            {
                throw new OmniBufException($"omnibuf: Wire override {ew} is not valid for byte[]");
            }

            kind = ScalarKind.Bytes;
            wireType = OmniBufWireType.Bytes;
            return true;
        }

        enumType = null;
        kind = ScalarKind.None;
        wireType = default;
        return false;
    }

    private static Action<OmniBufEncoder, object, bool, int> BuildFastFieldEncoder(FieldPlan plan)
    {
        uint fnum = plan.FieldNum;
        bool omitEmpty = plan.OmitEmpty;

        switch (plan.Category)
        {
            case FieldCategory.Scalar:
                return BuildScalarFieldEncoder(plan, fnum, omitEmpty);

            case FieldCategory.NullableScalar:
                return (enc, obj, _, _) =>
                {
                    object? val = plan.Getter(obj);
                    if (val is null)
                    {
                        return;
                    }

                    if (plan.Scalar == ScalarKind.Bool)
                    {
                        enc.WriteBoolField(fnum, val is true);
                        return;
                    }

                    enc.WriteTag(fnum, plan.WireType);
                    WriteScalarPayload(enc, plan.Scalar, plan.WireType, val);
                };

            case FieldCategory.SubMsg:
                return (enc, obj, det, depth) =>
                {
                    object? sub = plan.Getter(obj);
                    if (sub is null)
                    {
                        return;
                    }

                    enc.BeginSubMsgField(fnum);
                    plan.SubCodec!.EncodeObject(enc, sub, det, depth + 1);
                    enc.EndSubMsg();
                };

            case FieldCategory.List:
                if (TryBuildTypedListFieldEncoder(plan, fnum, omitEmpty, out Action<OmniBufEncoder, object, bool, int>? fastListEnc))
                {
                    return fastListEnc!;
                }

                goto case FieldCategory.Array;

            case FieldCategory.Array:
                return (enc, obj, det, depth) =>
                {
                    var list = plan.Getter(obj) as IList;
                    int count = list?.Count ?? 0;
                    if (omitEmpty && count == 0)
                    {
                        return;
                    }

                    if (depth + 1 > OmniBufConstants.MaxNestingDepth)
                    {
                        throw new OmniBufException($"omnibuf: WireArray encode depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
                    }

                    enc.BeginArrayField(fnum, plan.ElemWireType, count);
                    if (list is not null && count > 0)
                    {
                        if (plan.ElemWireType == OmniBufWireType.SubMsg)
                        {
                            MessageCodec subCodec = plan.SubCodec!;
                            for (int i = 0; i < count; i++)
                            {
                                enc.BeginContainer();
                                object? elem = list[i];
                                if (elem is not null)
                                {
                                    subCodec.EncodeObject(enc, elem, det, depth + 2);
                                }

                                enc.EndContainer();
                            }
                        }
                        else
                        {
                            for (int i = 0; i < count; i++)
                            {
                                WriteScalarPayload(enc, plan.ElemScalar, plan.ElemWireType, list[i]);
                            }
                        }
                    }

                    enc.EndArray();
                };

            case FieldCategory.Map:
                return (enc, obj, det, depth) =>
                {
                    var dict = plan.Getter(obj) as IDictionary;
                    int count = dict?.Count ?? 0;
                    if (omitEmpty && count == 0)
                    {
                        return;
                    }

                    if (depth + 1 > OmniBufConstants.MaxNestingDepth)
                    {
                        throw new OmniBufException($"omnibuf: WireMap encode depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
                    }

                    enc.BeginMapField(fnum, plan.MapKeyWireType, plan.MapValWireType, count);
                    if (dict is not null && count > 0)
                    {
                        if (det && count > 1)
                        {
                            var keys = new object[count];
                            dict.Keys.CopyTo(keys, 0);
                            SortMapKeys(keys, plan.MapKeyScalar);
                            foreach (object k in keys)
                            {
                                WriteScalarPayload(enc, plan.MapKeyScalar, plan.MapKeyWireType, k);
                                WriteMapValuePayload(enc, plan, dict[k], det, depth + 2);
                            }
                        }
                        else
                        {
                            foreach (DictionaryEntry entry in dict)
                            {
                                WriteScalarPayload(enc, plan.MapKeyScalar, plan.MapKeyWireType, entry.Key);
                                WriteMapValuePayload(enc, plan, entry.Value, det, depth + 2);
                            }
                        }
                    }

                    enc.EndMap();
                };

            case FieldCategory.NestedContainer:
                return (enc, obj, det, depth) =>
                {
                    object? container = plan.Getter(obj);
                    int count = container switch
                    {
                        IList l => l.Count,
                        IDictionary d => d.Count,
                        _ => 0,
                    };
                    if (omitEmpty && count == 0)
                    {
                        return;
                    }

                    enc.WriteTag(fnum, plan.WireType);
                    EncodeValueNode(enc, plan.ContainerNode!, container, det, depth + 1);
                };

            default:
                throw new OmniBufException($"omnibuf: unsupported field category {plan.Category}");
        }
    }

    private static void EncodeValueNode(OmniBufEncoder enc, ValueNode node, object? val, bool det, int depth)
    {
        if (depth > OmniBufConstants.MaxNestingDepth)
        {
            throw new OmniBufException($"omnibuf: container encode depth {depth} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
        }

        switch (node.Kind)
        {
            case ValueNodeKind.Scalar:
                WriteScalarPayload(enc, node.Scalar, node.WireType, val);
                break;

            case ValueNodeKind.SubMsg:
                enc.BeginContainer();
                if (val is not null)
                {
                    node.SubCodec!.EncodeObject(enc, val, det, depth);
                }

                enc.EndContainer();
                break;

            case ValueNodeKind.List:
            case ValueNodeKind.Array:
                {
                    var list = val as IList;
                    int count = list?.Count ?? 0;
                    ValueNode elemNode = node.ElemNode!;
                    enc.BeginArrayElement(elemNode.WireType, count);
                    if (list is not null)
                    {
                        int nextDepth = elemNode.Kind is ValueNodeKind.Scalar ? depth : depth + 1;
                        for (int i = 0; i < count; i++)
                        {
                            EncodeValueNode(enc, elemNode, list[i], det, nextDepth);
                        }
                    }

                    enc.EndArray();
                    break;
                }

            case ValueNodeKind.Map:
                {
                    var dict = val as IDictionary;
                    int count = dict?.Count ?? 0;
                    ValueNode valNode = node.MapValNode!;
                    enc.BeginMapElement(node.MapKeyWireType, valNode.WireType, count);
                    if (dict is not null && count > 0)
                    {
                        int nextDepth = valNode.Kind is ValueNodeKind.Scalar ? depth : depth + 1;
                        if (det && count > 1)
                        {
                            var keys = new object[count];
                            dict.Keys.CopyTo(keys, 0);
                            SortMapKeys(keys, node.MapKeyScalar);
                            foreach (object k in keys)
                            {
                                WriteScalarPayload(enc, node.MapKeyScalar, node.MapKeyWireType, k);
                                EncodeValueNode(enc, valNode, dict[k], det, nextDepth);
                            }
                        }
                        else
                        {
                            foreach (DictionaryEntry entry in dict)
                            {
                                WriteScalarPayload(enc, node.MapKeyScalar, node.MapKeyWireType, entry.Key);
                                EncodeValueNode(enc, valNode, entry.Value, det, nextDepth);
                            }
                        }
                    }

                    enc.EndMap();
                    break;
                }
        }
    }

    private static object DecodeValueNode(ReadOnlySpan<byte> data, ref int pos, ValueNode node, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            throw new OmniBufException($"omnibuf: container decode depth {depth} exceeds maximum {maxDepth}");
        }

        switch (node.Kind)
        {
            case ValueNodeKind.Scalar:
                return ReadScalarPayload(data, ref pos, node.Scalar, node.WireType, node.EnumType);

            case ValueNodeKind.SubMsg:
                {
                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
                    return node.SubCodec!.DecodeNew(sub, depth, maxDepth);
                }

            case ValueNodeKind.List:
                {
                    ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);
                    ValueNode elemNode = node.ElemNode!;
                    if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                        || elemWt != elemNode.WireType)
                    {
                        throw new OmniBufException("omnibuf: invalid nested WireArray header");
                    }

                    IList list = node.ListFactory!();
                    int nextDepth = elemNode.Kind is ValueNodeKind.Scalar ? depth : depth + 1;
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(DecodeValueNode(body, ref p, elemNode, nextDepth, maxDepth));
                    }

                    if (p != body.Length)
                    {
                        throw new OmniBufException("omnibuf: trailing bytes in nested WireArray");
                    }

                    return list;
                }

            case ValueNodeKind.Array:
                {
                    ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);
                    ValueNode elemNode = node.ElemNode!;
                    if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                        || elemWt != elemNode.WireType)
                    {
                        throw new OmniBufException("omnibuf: invalid nested WireArray header");
                    }

                    Array arr = Array.CreateInstance(elemNode.ClrType, count);
                    int nextDepth = elemNode.Kind is ValueNodeKind.Scalar ? depth : depth + 1;
                    for (int i = 0; i < count; i++)
                    {
                        arr.SetValue(DecodeValueNode(body, ref p, elemNode, nextDepth, maxDepth), i);
                    }

                    if (p != body.Length)
                    {
                        throw new OmniBufException("omnibuf: trailing bytes in nested WireArray");
                    }

                    return arr;
                }

            case ValueNodeKind.Map:
                {
                    ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 3);
                    ValueNode valNode = node.MapValNode!;
                    if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType kWt, out OmniBufWireType vWt, out int count, out int p)
                        || kWt != node.MapKeyWireType
                        || vWt != valNode.WireType)
                    {
                        throw new OmniBufException("omnibuf: invalid nested WireMap header");
                    }

                    IDictionary map = node.MapFactory!();
                    int nextDepth = valNode.Kind is ValueNodeKind.Scalar ? depth : depth + 1;
                    for (int i = 0; i < count; i++)
                    {
                        object k = ReadScalarPayload(body, ref p, node.MapKeyScalar, node.MapKeyWireType, node.MapKeyEnumType);
                        object v = DecodeValueNode(body, ref p, valNode, nextDepth, maxDepth);
                        map[k] = v;
                    }

                    if (p != body.Length)
                    {
                        throw new OmniBufException("omnibuf: trailing bytes in nested WireMap");
                    }

                    return map;
                }

            default:
                throw new OmniBufException($"omnibuf: unsupported container node {node.Kind}");
        }
    }

    private static Action<OmniBufEncoder, object, bool, int> BuildScalarFieldEncoder(FieldPlan plan, uint fnum, bool omitEmpty)
    {
        if (!plan.Property.DeclaringType!.IsValueType)
        {
            OmniBufWireType wt = plan.WireType;
            switch (plan.Scalar)
            {
                case ScalarKind.Bool:
                    {
                        Func<object, bool> get = CompileScalarAsGetter<bool>(plan.Property);
                        return (enc, obj, _, _) =>
                        {
                            bool v = get(obj);
                            if (omitEmpty && !v)
                            {
                                return;
                            }

                            enc.WriteBoolField(fnum, v);
                        };
                    }

                case ScalarKind.Byte:
                    {
                        Func<object, byte> get = CompileScalarAsGetter<byte>(plan.Property);
                        if (wt == OmniBufWireType.Sint)
                        {
                            return (enc, obj, _, _) =>
                            {
                                byte v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSintField(fnum, v);
                            };
                        }

                        return (enc, obj, _, _) =>
                        {
                            byte v = get(obj);
                            if (omitEmpty && v == 0)
                            {
                                return;
                            }

                            enc.WriteUintField(fnum, v);
                        };
                    }

                case ScalarKind.UInt16:
                    {
                        Func<object, ushort> get = CompileScalarAsGetter<ushort>(plan.Property);
                        if (wt == OmniBufWireType.Sint)
                        {
                            return (enc, obj, _, _) =>
                            {
                                ushort v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSintField(fnum, v);
                            };
                        }

                        return (enc, obj, _, _) =>
                        {
                            ushort v = get(obj);
                            if (omitEmpty && v == 0)
                            {
                                return;
                            }

                            enc.WriteUintField(fnum, v);
                        };
                    }

                case ScalarKind.UInt32:
                    {
                        Func<object, uint> get = CompileScalarAsGetter<uint>(plan.Property);
                        return wt switch
                        {
                            OmniBufWireType.Fixed32 => (enc, obj, _, _) =>
                            {
                                uint v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteFixed32Field(fnum, v);
                            }
                            ,
                            OmniBufWireType.Sfixed32 => (enc, obj, _, _) =>
                            {
                                uint v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSfixed32Field(fnum, unchecked((int)v));
                            }
                            ,
                            OmniBufWireType.Sint => (enc, obj, _, _) =>
                            {
                                uint v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSintField(fnum, v);
                            }
                            ,
                            _ => (enc, obj, _, _) =>
                            {
                                uint v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteUintField(fnum, v);
                            }
                            ,
                        };
                    }

                case ScalarKind.UInt64:
                    {
                        Func<object, ulong> get = CompileScalarAsGetter<ulong>(plan.Property);
                        return wt switch
                        {
                            OmniBufWireType.Fixed64 => (enc, obj, _, _) =>
                            {
                                ulong v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteFixed64Field(fnum, v);
                            }
                            ,
                            OmniBufWireType.Sfixed64 => (enc, obj, _, _) =>
                            {
                                ulong v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSfixed64Field(fnum, unchecked((long)v));
                            }
                            ,
                            OmniBufWireType.Sint => (enc, obj, _, _) =>
                            {
                                ulong v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSintField(fnum, unchecked((long)v));
                            }
                            ,
                            _ => (enc, obj, _, _) =>
                            {
                                ulong v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteUintField(fnum, v);
                            }
                            ,
                        };
                    }

                case ScalarKind.SByte:
                    {
                        Func<object, sbyte> get = CompileScalarAsGetter<sbyte>(plan.Property);
                        if (wt == OmniBufWireType.Uint)
                        {
                            return (enc, obj, _, _) =>
                            {
                                sbyte v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteUintField(fnum, unchecked((byte)v));
                            };
                        }

                        return (enc, obj, _, _) =>
                        {
                            sbyte v = get(obj);
                            if (omitEmpty && v == 0)
                            {
                                return;
                            }

                            enc.WriteSintField(fnum, v);
                        };
                    }

                case ScalarKind.Int16:
                    {
                        Func<object, short> get = CompileScalarAsGetter<short>(plan.Property);
                        if (wt == OmniBufWireType.Uint)
                        {
                            return (enc, obj, _, _) =>
                            {
                                short v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteUintField(fnum, unchecked((ushort)v));
                            };
                        }

                        return (enc, obj, _, _) =>
                        {
                            short v = get(obj);
                            if (omitEmpty && v == 0)
                            {
                                return;
                            }

                            enc.WriteSintField(fnum, v);
                        };
                    }

                case ScalarKind.Int32:
                    {
                        Func<object, int> get = CompileScalarAsGetter<int>(plan.Property);
                        return wt switch
                        {
                            OmniBufWireType.Sfixed32 => (enc, obj, _, _) =>
                            {
                                int v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSfixed32Field(fnum, v);
                            }
                            ,
                            OmniBufWireType.Fixed32 => (enc, obj, _, _) =>
                            {
                                int v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteFixed32Field(fnum, unchecked((uint)v));
                            }
                            ,
                            OmniBufWireType.Uint => (enc, obj, _, _) =>
                            {
                                int v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteUintField(fnum, unchecked((uint)v));
                            }
                            ,
                            _ => (enc, obj, _, _) =>
                            {
                                int v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSintField(fnum, v);
                            }
                            ,
                        };
                    }

                case ScalarKind.Int64:
                    {
                        Func<object, long> get = CompileScalarAsGetter<long>(plan.Property);
                        return wt switch
                        {
                            OmniBufWireType.Sfixed64 => (enc, obj, _, _) =>
                            {
                                long v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSfixed64Field(fnum, v);
                            }
                            ,
                            OmniBufWireType.Fixed64 => (enc, obj, _, _) =>
                            {
                                long v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteFixed64Field(fnum, unchecked((ulong)v));
                            }
                            ,
                            OmniBufWireType.Uint => (enc, obj, _, _) =>
                            {
                                long v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteUintField(fnum, unchecked((ulong)v));
                            }
                            ,
                            _ => (enc, obj, _, _) =>
                            {
                                long v = get(obj);
                                if (omitEmpty && v == 0)
                                {
                                    return;
                                }

                                enc.WriteSintField(fnum, v);
                            }
                            ,
                        };
                    }

                case ScalarKind.Float32:
                    {
                        Func<object, float> get = CompileTypedGetter<float>(plan.Property);
                        return (enc, obj, _, _) =>
                        {
                            float v = get(obj);
                            if (omitEmpty && BitConverter.SingleToUInt32Bits(v) == 0)
                            {
                                return;
                            }

                            enc.WriteFloat32Field(fnum, v);
                        };
                    }

                case ScalarKind.Float64:
                    {
                        Func<object, double> get = CompileTypedGetter<double>(plan.Property);
                        return (enc, obj, _, _) =>
                        {
                            double v = get(obj);
                            if (omitEmpty && BitConverter.DoubleToUInt64Bits(v) == 0)
                            {
                                return;
                            }

                            enc.WriteFloat64Field(fnum, v);
                        };
                    }

                case ScalarKind.String:
                    {
                        Func<object, string?> get = CompileTypedGetter<string?>(plan.Property);
                        return (enc, obj, _, _) =>
                        {
                            string? v = get(obj);
                            if (omitEmpty && string.IsNullOrEmpty(v))
                            {
                                return;
                            }

                            enc.WriteStringField(fnum, v);
                        };
                    }

                case ScalarKind.Bytes:
                    {
                        Func<object, byte[]?> get = CompileTypedGetter<byte[]?>(plan.Property);
                        return (enc, obj, _, _) =>
                        {
                            byte[]? v = get(obj);
                            if (omitEmpty && (v is null || v.Length == 0))
                            {
                                return;
                            }

                            enc.WriteBytesField(fnum, v ?? ReadOnlySpan<byte>.Empty);
                        };
                    }
            }
        }

        return (enc, obj, _, _) =>
        {
            object? val = plan.Getter(obj);
            if (omitEmpty && IsScalarDefault(plan.Scalar, val))
            {
                return;
            }

            if (plan.Scalar == ScalarKind.Bool)
            {
                enc.WriteBoolField(fnum, val is true);
                return;
            }

            enc.WriteTag(fnum, plan.WireType);
            WriteScalarPayload(enc, plan.Scalar, plan.WireType, val);
        };
    }

    private static bool TryBuildTypedListFieldEncoder(
        FieldPlan plan,
        uint fnum,
        bool omitEmpty,
        out Action<OmniBufEncoder, object, bool, int>? encoder)
    {
        encoder = null;
        if (plan.Property.DeclaringType!.IsValueType || plan.ElemType is null || plan.ElemEnumType is not null)
        {
            return false;
        }

        Type expectedListType = typeof(List<>).MakeGenericType(plan.ElemType);
        if (plan.PropertyType != expectedListType)
        {
            return false;
        }

        if (plan.ElemWireType == OmniBufWireType.SubMsg && !plan.ElemType.IsValueType)
        {
            MethodInfo helper = typeof(OmniBufSerializer)
                .GetMethod(nameof(BuildSubMsgListEncoderGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(plan.ElemType);
            encoder = (Action<OmniBufEncoder, object, bool, int>)helper.Invoke(null, new object[] { plan, fnum, omitEmpty })!;
            return true;
        }

        switch (plan.ElemScalar)
        {
            case ScalarKind.String:
                {
                    Func<object, List<string>?> get = CompileTypedGetter<List<string>?>(plan.Property);
                    encoder = (enc, obj, _, depth) =>
                    {
                        List<string>? list = get(obj);
                        int count = list?.Count ?? 0;
                        if (omitEmpty && count == 0)
                        {
                            return;
                        }

                        if (depth + 1 > OmniBufConstants.MaxNestingDepth)
                        {
                            throw new OmniBufException($"omnibuf: WireArray encode depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
                        }

                        enc.BeginArrayField(fnum, OmniBufWireType.String, count);
                        if (list is not null)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                enc.WriteStringValue(list[i]);
                            }
                        }

                        enc.EndArray();
                    };
                    return true;
                }

            case ScalarKind.UInt32 when plan.ElemWireType == OmniBufWireType.Uint:
                {
                    Func<object, List<uint>?> get = CompileTypedGetter<List<uint>?>(plan.Property);
                    encoder = (enc, obj, _, depth) =>
                    {
                        List<uint>? list = get(obj);
                        int count = list?.Count ?? 0;
                        if (omitEmpty && count == 0)
                        {
                            return;
                        }

                        if (depth + 1 > OmniBufConstants.MaxNestingDepth)
                        {
                            throw new OmniBufException($"omnibuf: WireArray encode depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
                        }

                        enc.BeginArrayField(fnum, OmniBufWireType.Uint, count);
                        if (list is not null)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                enc.WriteUintValue(list[i]);
                            }
                        }

                        enc.EndArray();
                    };
                    return true;
                }

            case ScalarKind.Int32 when plan.ElemWireType == OmniBufWireType.Sint:
                {
                    Func<object, List<int>?> get = CompileTypedGetter<List<int>?>(plan.Property);
                    encoder = (enc, obj, _, depth) =>
                    {
                        List<int>? list = get(obj);
                        int count = list?.Count ?? 0;
                        if (omitEmpty && count == 0)
                        {
                            return;
                        }

                        if (depth + 1 > OmniBufConstants.MaxNestingDepth)
                        {
                            throw new OmniBufException($"omnibuf: WireArray encode depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
                        }

                        enc.BeginArrayField(fnum, OmniBufWireType.Sint, count);
                        if (list is not null)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                enc.WriteSintValue(list[i]);
                            }
                        }

                        enc.EndArray();
                    };
                    return true;
                }
        }

        return false;
    }

    private static Action<OmniBufEncoder, object, bool, int> BuildSubMsgListEncoderGeneric<TElem>(FieldPlan plan, uint fnum, bool omitEmpty)
        where TElem : class
    {
        Func<object, List<TElem>?> get = CompileTypedGetter<List<TElem>?>(plan.Property);
        MessageCodec subCodec = plan.SubCodec!;
        return (enc, obj, det, depth) =>
        {
            List<TElem>? list = get(obj);
            int count = list?.Count ?? 0;
            if (omitEmpty && count == 0)
            {
                return;
            }

            if (depth + 1 > OmniBufConstants.MaxNestingDepth)
            {
                throw new OmniBufException($"omnibuf: WireArray encode depth {depth + 1} exceeds maximum {OmniBufConstants.MaxNestingDepth}");
            }

            enc.BeginArrayField(fnum, OmniBufWireType.SubMsg, count);
            if (list is not null)
            {
                for (int i = 0; i < count; i++)
                {
                    enc.BeginContainer();
                    TElem? elem = list[i];
                    if (elem is not null)
                    {
                        subCodec.EncodeObject(enc, elem, det, depth + 2);
                    }

                    enc.EndContainer();
                }
            }

            enc.EndArray();
        };
    }

    private static bool IsScalarDefault(ScalarKind kind, object? val) => kind switch
    {
        ScalarKind.Bool => val is not true,
        ScalarKind.Byte => val is null || UnboxByte(val) == 0,
        ScalarKind.UInt16 => val is null || UnboxUInt16(val) == 0,
        ScalarKind.UInt32 => val is null || UnboxUInt32(val) == 0,
        ScalarKind.UInt64 => val is null || UnboxUInt64(val) == 0,
        ScalarKind.SByte => val is null || UnboxSByte(val) == 0,
        ScalarKind.Int16 => val is null || UnboxInt16(val) == 0,
        ScalarKind.Int32 => val is null || UnboxInt32(val) == 0,
        ScalarKind.Int64 => val is null || UnboxInt64(val) == 0,
        ScalarKind.Float32 => val is null || BitConverter.SingleToUInt32Bits((float)val) == 0,
        ScalarKind.Float64 => val is null || BitConverter.DoubleToUInt64Bits((double)val) == 0,
        ScalarKind.String => string.IsNullOrEmpty(val as string),
        ScalarKind.Bytes => val is not byte[] b || b.Length == 0,
        _ => val is null,
    };

    private static byte UnboxByte(object? val) => val is null ? (byte)0 : (val is byte b ? b : Convert.ToByte(val));
    private static ushort UnboxUInt16(object? val) => val is null ? (ushort)0 : (val is ushort u ? u : Convert.ToUInt16(val));
    private static uint UnboxUInt32(object? val) => val is null ? 0u : (val is uint u ? u : Convert.ToUInt32(val));
    private static ulong UnboxUInt64(object? val) => val is null ? 0UL : (val is ulong u ? u : Convert.ToUInt64(val));
    private static sbyte UnboxSByte(object? val) => val is null ? (sbyte)0 : (val is sbyte s ? s : Convert.ToSByte(val));
    private static short UnboxInt16(object? val) => val is null ? (short)0 : (val is short s ? s : Convert.ToInt16(val));
    private static int UnboxInt32(object? val) => val is null ? 0 : (val is int i ? i : Convert.ToInt32(val));
    private static long UnboxInt64(object? val) => val is null ? 0L : (val is long l ? l : Convert.ToInt64(val));

    private static void WriteMapValuePayload(OmniBufEncoder enc, FieldPlan plan, object? val, bool det, int childDepth)
    {
        if (plan.MapValWireType == OmniBufWireType.SubMsg)
        {
            enc.BeginContainer();
            if (val is not null)
            {
                plan.SubCodec!.EncodeObject(enc, val, det, childDepth);
            }

            enc.EndContainer();
        }
        else
        {
            WriteScalarPayload(enc, plan.MapValScalar, plan.MapValWireType, val);
        }
    }

    private static void WriteScalarPayload(OmniBufEncoder enc, ScalarKind kind, OmniBufWireType wt, object? val)
    {
        switch (kind)
        {
            case ScalarKind.Bool:
                enc.WriteBoolElement(val is true);
                break;

            case ScalarKind.Byte:
                {
                    byte b = UnboxByte(val);
                    if (wt == OmniBufWireType.Sint)
                    {
                        enc.WriteSintValue(b);
                    }
                    else
                    {
                        enc.WriteUintValue(b);
                    }

                    break;
                }

            case ScalarKind.UInt16:
                {
                    ushort u16 = UnboxUInt16(val);
                    if (wt == OmniBufWireType.Sint)
                    {
                        enc.WriteSintValue(u16);
                    }
                    else
                    {
                        enc.WriteUintValue(u16);
                    }

                    break;
                }

            case ScalarKind.UInt32:
                {
                    uint u32 = UnboxUInt32(val);
                    switch (wt)
                    {
                        case OmniBufWireType.Fixed32:
                            enc.WriteFixed32Value(u32);
                            break;
                        case OmniBufWireType.Sfixed32:
                            enc.WriteSfixed32Value(unchecked((int)u32));
                            break;
                        case OmniBufWireType.Sint:
                            enc.WriteSintValue(u32);
                            break;
                        default:
                            enc.WriteUintValue(u32);
                            break;
                    }

                    break;
                }

            case ScalarKind.UInt64:
                {
                    ulong u64 = UnboxUInt64(val);
                    switch (wt)
                    {
                        case OmniBufWireType.Fixed64:
                            enc.WriteFixed64Value(u64);
                            break;
                        case OmniBufWireType.Sfixed64:
                            enc.WriteSfixed64Value(unchecked((long)u64));
                            break;
                        case OmniBufWireType.Sint:
                            enc.WriteSintValue(unchecked((long)u64));
                            break;
                        default:
                            enc.WriteUintValue(u64);
                            break;
                    }

                    break;
                }

            case ScalarKind.SByte:
                {
                    sbyte sb = UnboxSByte(val);
                    if (wt == OmniBufWireType.Uint)
                    {
                        enc.WriteUintValue(unchecked((byte)sb));
                    }
                    else
                    {
                        enc.WriteSintValue(sb);
                    }

                    break;
                }

            case ScalarKind.Int16:
                {
                    short i16 = UnboxInt16(val);
                    if (wt == OmniBufWireType.Uint)
                    {
                        enc.WriteUintValue(unchecked((ushort)i16));
                    }
                    else
                    {
                        enc.WriteSintValue(i16);
                    }

                    break;
                }

            case ScalarKind.Int32:
                {
                    int i32 = UnboxInt32(val);
                    switch (wt)
                    {
                        case OmniBufWireType.Sfixed32:
                            enc.WriteSfixed32Value(i32);
                            break;
                        case OmniBufWireType.Fixed32:
                            enc.WriteFixed32Value(unchecked((uint)i32));
                            break;
                        case OmniBufWireType.Uint:
                            enc.WriteUintValue(unchecked((uint)i32));
                            break;
                        default:
                            enc.WriteSintValue(i32);
                            break;
                    }

                    break;
                }

            case ScalarKind.Int64:
                {
                    long i64 = UnboxInt64(val);
                    switch (wt)
                    {
                        case OmniBufWireType.Sfixed64:
                            enc.WriteSfixed64Value(i64);
                            break;
                        case OmniBufWireType.Fixed64:
                            enc.WriteFixed64Value(unchecked((ulong)i64));
                            break;
                        case OmniBufWireType.Uint:
                            enc.WriteUintValue(unchecked((ulong)i64));
                            break;
                        default:
                            enc.WriteSintValue(i64);
                            break;
                    }

                    break;
                }

            case ScalarKind.Float32:
                enc.WriteFloat32Value(val is float f ? f : 0f);
                break;

            case ScalarKind.Float64:
                enc.WriteFloat64Value(val is double d ? d : 0d);
                break;

            case ScalarKind.String:
                enc.WriteStringValue(val as string);
                break;

            case ScalarKind.Bytes:
                enc.WriteBytesValue(val is byte[] bytes ? bytes : ReadOnlySpan<byte>.Empty);
                break;

            default:
                throw new OmniBufException($"omnibuf: unsupported scalar kind {kind}");
        }
    }

    private static object? DecodeFieldValue(
        ReadOnlySpan<byte> data,
        ref int pos,
        OmniBufWireType wt,
        FieldPlan plan,
        object? existing,
        bool alreadySeen,
        int depth,
        int maxDepth,
        ref bool markedSeen)
    {
        switch (plan.Category)
        {
            case FieldCategory.Scalar:
            case FieldCategory.NullableScalar:
                {
                    if (plan.Scalar == ScalarKind.Bool)
                    {
                        if (wt != OmniBufWireType.BoolFalse && wt != OmniBufWireType.BoolTrue)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return existing;
                        }

                        markedSeen = true;
                        bool bVal = wt == OmniBufWireType.BoolTrue;
                        return plan.EnumType is not null ? Enum.ToObject(plan.EnumType, bVal ? 1 : 0) : bVal;
                    }

                    if (wt != plan.WireType)
                    {
                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                        return existing;
                    }

                    markedSeen = true;
                    return ReadScalarPayload(data, ref pos, plan.Scalar, plan.WireType, plan.EnumType);
                }

            case FieldCategory.SubMsg:
                {
                    if (wt != OmniBufWireType.SubMsg)
                    {
                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                        return existing;
                    }

                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
                    markedSeen = true;
                    if (alreadySeen && existing is not null && (!plan.PropertyType.IsValueType || plan.IsNullableValueType) && plan.SubCodec is not null)
                    {
                        return plan.SubCodec.DecodeMerge(sub, existing, depth + 1, maxDepth);
                    }

                    return plan.SubCodec!.DecodeNew(sub, depth + 1, maxDepth);
                }

            case FieldCategory.List:
            case FieldCategory.Array:
                {
                    if (wt != OmniBufWireType.Array)
                    {
                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                        return existing;
                    }

                    if (depth + 1 > maxDepth)
                    {
                        throw new OmniBufException($"omnibuf: WireArray depth {depth + 1} exceeds maximum {maxDepth}");
                    }

                    ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);
                    if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                        || elemWt != plan.ElemWireType)
                    {
                        throw new OmniBufException($"omnibuf: invalid WireArray header on field #{plan.FieldNum}");
                    }

                    markedSeen = true;
                    if (plan.Category == FieldCategory.List)
                    {
                        IList list = existing as IList ?? plan.ListFactory!();
                        if (!alreadySeen)
                        {
                            list.Clear();
                        }

                        AppendArrayElementsToList(body, ref p, count, plan, list, depth, maxDepth);
                        if (p != body.Length)
                        {
                            throw new OmniBufException("omnibuf: trailing bytes in WireArray");
                        }

                        return list;
                    }
                    else
                    {
                        Array? prevArr = alreadySeen ? existing as Array : null;
                        int prevLen = prevArr?.Length ?? 0;
                        Array nextArr = Array.CreateInstance(plan.ElemType!, prevLen + count);
                        if (prevArr is not null && prevLen > 0)
                        {
                            Array.Copy(prevArr, nextArr, prevLen);
                        }

                        for (int i = 0; i < count; i++)
                        {
                            object elem = plan.ElemWireType == OmniBufWireType.SubMsg
                                ? plan.SubCodec!.DecodeNew(OmniBufDecoder.ReadLenSlice(body, ref p, 0), depth + 2, maxDepth)
                                : ReadScalarPayload(body, ref p, plan.ElemScalar, plan.ElemWireType, plan.ElemEnumType);
                            nextArr.SetValue(elem, prevLen + i);
                        }

                        if (p != body.Length)
                        {
                            throw new OmniBufException("omnibuf: trailing bytes in WireArray");
                        }

                        return nextArr;
                    }
                }

            case FieldCategory.Map:
                {
                    if (wt != OmniBufWireType.Map)
                    {
                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                        return existing;
                    }

                    if (depth + 1 > maxDepth)
                    {
                        throw new OmniBufException($"omnibuf: WireMap depth {depth + 1} exceeds maximum {maxDepth}");
                    }

                    ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 3);
                    if (!OmniBufDecoder.TryParseMapHeader(body, out OmniBufWireType kWt, out OmniBufWireType vWt, out int count, out int p)
                        || kWt != plan.MapKeyWireType
                        || vWt != plan.MapValWireType)
                    {
                        throw new OmniBufException($"omnibuf: invalid WireMap header on field #{plan.FieldNum}");
                    }

                    markedSeen = true;
                    IDictionary map = existing as IDictionary ?? plan.MapFactory!();
                    if (!alreadySeen)
                    {
                        map.Clear();
                    }

                    for (int i = 0; i < count; i++)
                    {
                        object key = ReadScalarPayload(body, ref p, plan.MapKeyScalar, plan.MapKeyWireType, plan.MapKeyEnumType);
                        object val = plan.MapValWireType == OmniBufWireType.SubMsg
                            ? plan.SubCodec!.DecodeNew(OmniBufDecoder.ReadLenSlice(body, ref p, 0), depth + 2, maxDepth)
                            : ReadScalarPayload(body, ref p, plan.MapValScalar, plan.MapValWireType, plan.MapValEnumType);
                        map[key] = val;
                    }

                    if (p != body.Length)
                    {
                        throw new OmniBufException("omnibuf: trailing bytes in WireMap");
                    }

                    return map;
                }

            case FieldCategory.NestedContainer:
                {
                    if (wt != plan.WireType)
                    {
                        OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                        return existing;
                    }

                    markedSeen = true;
                    object decoded = DecodeValueNode(data, ref pos, plan.ContainerNode!, depth + 1, maxDepth);
                    if (!alreadySeen || existing is null)
                    {
                        return decoded;
                    }

                    if (plan.ContainerNode!.Kind == ValueNodeKind.List && existing is IList prevList && decoded is IList nextList)
                    {
                        foreach (object? item in nextList)
                        {
                            prevList.Add(item);
                        }

                        return prevList;
                    }

                    if (plan.ContainerNode.Kind == ValueNodeKind.Array && existing is Array prevArr && decoded is Array nextArr)
                    {
                        Array merged = Array.CreateInstance(plan.ElemType!, prevArr.Length + nextArr.Length);
                        Array.Copy(prevArr, merged, prevArr.Length);
                        Array.Copy(nextArr, 0, merged, prevArr.Length, nextArr.Length);
                        return merged;
                    }

                    if (plan.ContainerNode.Kind == ValueNodeKind.Map && existing is IDictionary prevMap && decoded is IDictionary nextMap)
                    {
                        foreach (DictionaryEntry kv in nextMap)
                        {
                            prevMap[kv.Key] = kv.Value;
                        }

                        return prevMap;
                    }

                    return decoded;
                }

            default:
                throw new OmniBufException($"omnibuf: unsupported field category {plan.Category}");
        }
    }

    private static void AppendArrayElementsToList(
        ReadOnlySpan<byte> body,
        ref int p,
        int count,
        FieldPlan plan,
        IList list,
        int depth,
        int maxDepth)
    {
        if (plan.ElemWireType == OmniBufWireType.SubMsg)
        {
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);
                list.Add(plan.SubCodec!.DecodeNew(sub, depth + 2, maxDepth));
            }

            return;
        }

        for (int i = 0; i < count; i++)
        {
            list.Add(ReadScalarPayload(body, ref p, plan.ElemScalar, plan.ElemWireType, plan.ElemEnumType));
        }
    }

    private static object ReadScalarPayload(
        ReadOnlySpan<byte> data,
        ref int pos,
        ScalarKind kind,
        OmniBufWireType wt,
        Type? enumType = null)
    {
        object raw = ReadUnderlyingScalarPayload(data, ref pos, kind, wt);
        return enumType is not null ? Enum.ToObject(enumType, raw) : raw;
    }

    private static object ReadUnderlyingScalarPayload(ReadOnlySpan<byte> data, ref int pos, ScalarKind kind, OmniBufWireType wt)
    {
        switch (kind)
        {
            case ScalarKind.Bool:
                return OmniBufDecoder.ReadBoolElement(data, ref pos);

            case ScalarKind.Byte:
                {
                    if (wt == OmniBufWireType.Sint)
                    {
                        long s = OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));
                        if (s < 0 || s > byte.MaxValue)
                        {
                            throw new OmniBufException($"omnibuf: uint8 overflow ({s})");
                        }

                        return (byte)s;
                    }

                    ulong v = OmniBufDecoder.ReadVarint(data, ref pos);
                    if (v > byte.MaxValue)
                    {
                        throw new OmniBufException($"omnibuf: uint8 overflow ({v})");
                    }

                    return (byte)v;
                }

            case ScalarKind.UInt16:
                {
                    if (wt == OmniBufWireType.Sint)
                    {
                        long s = OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));
                        if (s < 0 || s > ushort.MaxValue)
                        {
                            throw new OmniBufException($"omnibuf: uint16 overflow ({s})");
                        }

                        return (ushort)s;
                    }

                    ulong v = OmniBufDecoder.ReadVarint(data, ref pos);
                    if (v > ushort.MaxValue)
                    {
                        throw new OmniBufException($"omnibuf: uint16 overflow ({v})");
                    }

                    return (ushort)v;
                }

            case ScalarKind.UInt32:
                return wt switch
                {
                    OmniBufWireType.Fixed32 or OmniBufWireType.Sfixed32 => OmniBufDecoder.ReadFixed32(data, ref pos),
                    OmniBufWireType.Sint => unchecked((uint)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos))),
                    _ => (uint)OmniBufDecoder.ReadVarint(data, ref pos),
                };

            case ScalarKind.UInt64:
                return wt switch
                {
                    OmniBufWireType.Fixed64 or OmniBufWireType.Sfixed64 => OmniBufDecoder.ReadFixed64(data, ref pos),
                    OmniBufWireType.Sint => unchecked((ulong)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos))),
                    _ => OmniBufDecoder.ReadVarint(data, ref pos),
                };

            case ScalarKind.SByte:
                {
                    if (wt == OmniBufWireType.Uint)
                    {
                        ulong u = OmniBufDecoder.ReadVarint(data, ref pos);
                        if (u > byte.MaxValue)
                        {
                            throw new OmniBufException($"omnibuf: int8 overflow ({u})");
                        }

                        return unchecked((sbyte)(byte)u);
                    }

                    long v = OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));
                    if (v < sbyte.MinValue || v > sbyte.MaxValue)
                    {
                        throw new OmniBufException($"omnibuf: int8 overflow ({v})");
                    }

                    return (sbyte)v;
                }

            case ScalarKind.Int16:
                {
                    if (wt == OmniBufWireType.Uint)
                    {
                        ulong u = OmniBufDecoder.ReadVarint(data, ref pos);
                        if (u > ushort.MaxValue)
                        {
                            throw new OmniBufException($"omnibuf: int16 overflow ({u})");
                        }

                        return unchecked((short)(ushort)u);
                    }

                    long v = OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos));
                    if (v < short.MinValue || v > short.MaxValue)
                    {
                        throw new OmniBufException($"omnibuf: int16 overflow ({v})");
                    }

                    return (short)v;
                }

            case ScalarKind.Int32:
                return wt switch
                {
                    OmniBufWireType.Sfixed32 or OmniBufWireType.Fixed32 => unchecked((int)OmniBufDecoder.ReadFixed32(data, ref pos)),
                    OmniBufWireType.Uint => unchecked((int)(uint)OmniBufDecoder.ReadVarint(data, ref pos)),
                    _ => (int)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos)),
                };

            case ScalarKind.Int64:
                return wt switch
                {
                    OmniBufWireType.Sfixed64 or OmniBufWireType.Fixed64 => unchecked((long)OmniBufDecoder.ReadFixed64(data, ref pos)),
                    OmniBufWireType.Uint => unchecked((long)OmniBufDecoder.ReadVarint(data, ref pos)),
                    _ => OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos)),
                };

            case ScalarKind.Float32:
                return BitConverter.UInt32BitsToSingle(OmniBufDecoder.ReadFixed32(data, ref pos));

            case ScalarKind.Float64:
                return BitConverter.UInt64BitsToDouble(OmniBufDecoder.ReadFixed64(data, ref pos));

            case ScalarKind.String:
                return OmniBufDecoder.ReadString(data, ref pos);

            case ScalarKind.Bytes:
                return OmniBufDecoder.ReadLenSlice(data, ref pos, 0).ToArray();

            default:
                throw new OmniBufException($"omnibuf: unsupported scalar kind {kind}");
        }
    }

    private static object? CreateDefaultFieldValue(FieldPlan plan) => plan.Category switch
    {
        FieldCategory.NullableScalar => null,
        FieldCategory.Scalar when plan.EnumType is not null => Enum.ToObject(plan.EnumType, 0),
        FieldCategory.Scalar => plan.Scalar switch
        {
            ScalarKind.Bool => false,
            ScalarKind.Byte => (byte)0,
            ScalarKind.UInt16 => (ushort)0,
            ScalarKind.UInt32 => 0u,
            ScalarKind.UInt64 => 0UL,
            ScalarKind.SByte => (sbyte)0,
            ScalarKind.Int16 => (short)0,
            ScalarKind.Int32 => 0,
            ScalarKind.Int64 => 0L,
            ScalarKind.Float32 => 0f,
            ScalarKind.Float64 => 0d,
            ScalarKind.String => string.Empty,
            ScalarKind.Bytes => Array.Empty<byte>(),
            _ => null,
        },
        FieldCategory.SubMsg => plan.IsNullableValueType ? null : (plan.PropertyType.IsValueType ? Activator.CreateInstance(plan.PropertyType) : null),
        FieldCategory.List => plan.ListFactory!(),
        FieldCategory.Array => Array.CreateInstance(plan.ElemType!, 0),
        FieldCategory.Map => plan.MapFactory!(),
        FieldCategory.NestedContainer => plan.ContainerNode!.Kind switch
        {
            ValueNodeKind.List => plan.ContainerNode.ListFactory!(),
            ValueNodeKind.Array => Array.CreateInstance(plan.ElemType!, 0),
            ValueNodeKind.Map => plan.ContainerNode.MapFactory!(),
            _ => null,
        },
        _ => null,
    };

    private static void SortMapKeys(object[] keys, ScalarKind keyScalar)
    {
        Array.Sort(keys, (a, b) => keyScalar switch
        {
            ScalarKind.String => StringComparer.Ordinal.Compare((string)a, (string)b),
            ScalarKind.Bool => ((bool)a).CompareTo((bool)b),
            ScalarKind.Byte => UnboxByte(a).CompareTo(UnboxByte(b)),
            ScalarKind.UInt16 => UnboxUInt16(a).CompareTo(UnboxUInt16(b)),
            ScalarKind.UInt32 => UnboxUInt32(a).CompareTo(UnboxUInt32(b)),
            ScalarKind.UInt64 => UnboxUInt64(a).CompareTo(UnboxUInt64(b)),
            ScalarKind.SByte => UnboxSByte(a).CompareTo(UnboxSByte(b)),
            ScalarKind.Int16 => UnboxInt16(a).CompareTo(UnboxInt16(b)),
            ScalarKind.Int32 => UnboxInt32(a).CompareTo(UnboxInt32(b)),
            ScalarKind.Int64 => UnboxInt64(a).CompareTo(UnboxInt64(b)),
            _ => Comparer.DefaultInvariant.Compare(a, b),
        });
    }

    private static Func<object, object?> CompileGetter(PropertyInfo prop)
    {
        MethodInfo? getMethod = prop.GetGetMethod();
        if (getMethod is null)
        {
            throw new OmniBufException($"omnibuf: property {prop.DeclaringType?.Name}.{prop.Name} must have a public getter");
        }

        ParameterExpression objParam = Expression.Parameter(typeof(object), "obj");
        Expression castTarget = prop.DeclaringType!.IsValueType
            ? Expression.Unbox(objParam, prop.DeclaringType)
            : Expression.Convert(objParam, prop.DeclaringType);
        MemberExpression access = Expression.Property(castTarget, prop);
        UnaryExpression boxed = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(boxed, objParam).Compile();
    }

    private static Action<object, object?>? CompileSetter(PropertyInfo prop)
    {
        MethodInfo? setMethod = prop.GetSetMethod();
        if (setMethod is null)
        {
            return null;
        }

        // Reject C# init-only setters for post-construction mutation.
        Type[] modReq = setMethod.ReturnParameter.GetRequiredCustomModifiers();
        if (Array.Exists(modReq, t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit"))
        {
            return null;
        }

        if (prop.DeclaringType!.IsValueType)
        {
            return (obj, val) => prop.SetValue(obj, val);
        }

        ParameterExpression objParam = Expression.Parameter(typeof(object), "obj");
        ParameterExpression valParam = Expression.Parameter(typeof(object), "val");
        UnaryExpression castObj = Expression.Convert(objParam, prop.DeclaringType);
        UnaryExpression castVal = Expression.Convert(valParam, prop.PropertyType);
        MethodCallExpression call = Expression.Call(castObj, setMethod, castVal);
        return Expression.Lambda<Action<object, object?>>(call, objParam, valParam).Compile();
    }

    private static Func<object, TProp> CompileTypedGetter<TProp>(PropertyInfo prop)
    {
        ParameterExpression objParam = Expression.Parameter(typeof(object), "obj");
        UnaryExpression castObj = Expression.Convert(objParam, prop.DeclaringType!);
        MemberExpression access = Expression.Property(castObj, prop);
        return Expression.Lambda<Func<object, TProp>>(access, objParam).Compile();
    }

    private static Func<object, TScalar> CompileScalarAsGetter<TScalar>(PropertyInfo prop)
    {
        ParameterExpression objParam = Expression.Parameter(typeof(object), "obj");
        UnaryExpression castObj = Expression.Convert(objParam, prop.DeclaringType!);
        Expression access = Expression.Property(castObj, prop);
        if (access.Type != typeof(TScalar))
        {
            access = Expression.Convert(access, typeof(TScalar));
        }

        return Expression.Lambda<Func<object, TScalar>>(access, objParam).Compile();
    }

    private static Action<object, TProp> CompileTypedSetter<TProp>(PropertyInfo prop)
    {
        MethodInfo setMethod = prop.GetSetMethod()!;
        ParameterExpression objParam = Expression.Parameter(typeof(object), "obj");
        ParameterExpression valParam = Expression.Parameter(typeof(TProp), "val");
        UnaryExpression castObj = Expression.Convert(objParam, prop.DeclaringType!);
        MethodCallExpression call = Expression.Call(castObj, setMethod, valParam);
        return Expression.Lambda<Action<object, TProp>>(call, objParam, valParam).Compile();
    }

    private static Action<object, TScalar> CompileScalarFromSetter<TScalar>(PropertyInfo prop)
    {
        MethodInfo setMethod = prop.GetSetMethod()!;
        ParameterExpression objParam = Expression.Parameter(typeof(object), "obj");
        ParameterExpression valParam = Expression.Parameter(typeof(TScalar), "val");
        UnaryExpression castObj = Expression.Convert(objParam, prop.DeclaringType!);
        Expression arg = prop.PropertyType == typeof(TScalar)
            ? valParam
            : Expression.Convert(valParam, prop.PropertyType);
        MethodCallExpression call = Expression.Call(castObj, setMethod, arg);
        return Expression.Lambda<Action<object, TScalar>>(call, objParam, valParam).Compile();
    }

    private static FastInPlaceDecodeDelegate? BuildFastInPlaceDecoder(FieldPlan plan)
    {
        if (plan.Property.DeclaringType!.IsValueType || plan.Setter is null)
        {
            return null;
        }

        switch (plan.Category)
        {
            case FieldCategory.Scalar:
                return BuildFastScalarDecoder(plan);

            case FieldCategory.SubMsg when !plan.PropertyType.IsValueType && plan.SubCodec is not null:
                {
                    Func<object, object?> get = plan.Getter;
                    Action<object, object?> set = plan.Setter;
                    MessageCodec subCodec = plan.SubCodec;
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool alreadySeen, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.SubMsg)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(data, ref pos, 0);
                        markedSeen = true;
                        object? cur = get(target);
                        if (subCodec.CanDecodeInPlace)
                        {
                            if (cur is null)
                            {
                                cur = subCodec.CreateBlank();
                                set(target, cur);
                            }

                            subCodec.DecodeInto(sub, cur, depth + 1, maxDepth, merge: alreadySeen);
                            return;
                        }

                        object next = (alreadySeen && cur is not null)
                            ? subCodec.DecodeMerge(sub, cur, depth + 1, maxDepth)
                            : subCodec.DecodeNew(sub, depth + 1, maxDepth);
                        set(target, next);
                    };
                }

            case FieldCategory.List when plan.ElemType is not null && plan.ElemEnumType is null && plan.PropertyType == typeof(List<>).MakeGenericType(plan.ElemType):
                return BuildFastTypedListDecoder(plan);

            default:
                return null;
        }
    }

    private static FastInPlaceDecodeDelegate? BuildFastScalarDecoder(FieldPlan plan)
    {
        OmniBufWireType expectedWt = plan.WireType;
        switch (plan.Scalar)
        {
            case ScalarKind.Bool:
                {
                    Action<object, bool> set = CompileScalarFromSetter<bool>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.BoolFalse && wt != OmniBufWireType.BoolTrue)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        markedSeen = true;
                        set(target, wt == OmniBufWireType.BoolTrue);
                    };
                }

            case ScalarKind.Byte:
                {
                    Action<object, byte> set = CompileScalarFromSetter<byte>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != expectedWt)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        byte b = (byte)ReadUnderlyingScalarPayload(data, ref pos, ScalarKind.Byte, expectedWt);
                        markedSeen = true;
                        set(target, b);
                    };
                }

            case ScalarKind.UInt16:
                {
                    Action<object, ushort> set = CompileScalarFromSetter<ushort>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != expectedWt)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        ushort u = (ushort)ReadUnderlyingScalarPayload(data, ref pos, ScalarKind.UInt16, expectedWt);
                        markedSeen = true;
                        set(target, u);
                    };
                }

            case ScalarKind.UInt32:
                {
                    Action<object, uint> set = CompileScalarFromSetter<uint>(plan.Property);
                    if (expectedWt is OmniBufWireType.Fixed32 or OmniBufWireType.Sfixed32)
                    {
                        return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                        {
                            if (wt != expectedWt)
                            {
                                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                                return;
                            }

                            markedSeen = true;
                            set(target, OmniBufDecoder.ReadFixed32(data, ref pos));
                        };
                    }

                    if (expectedWt == OmniBufWireType.Sint)
                    {
                        return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                        {
                            if (wt != OmniBufWireType.Sint)
                            {
                                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                                return;
                            }

                            markedSeen = true;
                            set(target, unchecked((uint)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos))));
                        };
                    }

                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Uint)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        markedSeen = true;
                        set(target, (uint)OmniBufDecoder.ReadVarint(data, ref pos));
                    };
                }

            case ScalarKind.UInt64:
                {
                    Action<object, ulong> set = CompileScalarFromSetter<ulong>(plan.Property);
                    if (expectedWt is OmniBufWireType.Fixed64 or OmniBufWireType.Sfixed64)
                    {
                        return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                        {
                            if (wt != expectedWt)
                            {
                                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                                return;
                            }

                            markedSeen = true;
                            set(target, OmniBufDecoder.ReadFixed64(data, ref pos));
                        };
                    }

                    if (expectedWt == OmniBufWireType.Sint)
                    {
                        return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                        {
                            if (wt != OmniBufWireType.Sint)
                            {
                                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                                return;
                            }

                            markedSeen = true;
                            set(target, unchecked((ulong)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos))));
                        };
                    }

                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Uint)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        markedSeen = true;
                        set(target, OmniBufDecoder.ReadVarint(data, ref pos));
                    };
                }

            case ScalarKind.SByte:
                {
                    Action<object, sbyte> set = CompileScalarFromSetter<sbyte>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != expectedWt)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        sbyte s = (sbyte)ReadUnderlyingScalarPayload(data, ref pos, ScalarKind.SByte, expectedWt);
                        markedSeen = true;
                        set(target, s);
                    };
                }

            case ScalarKind.Int16:
                {
                    Action<object, short> set = CompileScalarFromSetter<short>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != expectedWt)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        short s = (short)ReadUnderlyingScalarPayload(data, ref pos, ScalarKind.Int16, expectedWt);
                        markedSeen = true;
                        set(target, s);
                    };
                }

            case ScalarKind.Int32:
                {
                    Action<object, int> set = CompileScalarFromSetter<int>(plan.Property);
                    if (expectedWt is OmniBufWireType.Sfixed32 or OmniBufWireType.Fixed32)
                    {
                        return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                        {
                            if (wt != expectedWt)
                            {
                                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                                return;
                            }

                            markedSeen = true;
                            set(target, unchecked((int)OmniBufDecoder.ReadFixed32(data, ref pos)));
                        };
                    }

                    if (expectedWt == OmniBufWireType.Uint)
                    {
                        return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                        {
                            if (wt != OmniBufWireType.Uint)
                            {
                                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                                return;
                            }

                            markedSeen = true;
                            set(target, unchecked((int)(uint)OmniBufDecoder.ReadVarint(data, ref pos)));
                        };
                    }

                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Sint)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        markedSeen = true;
                        set(target, (int)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos)));
                    };
                }

            case ScalarKind.Int64:
                {
                    Action<object, long> set = CompileScalarFromSetter<long>(plan.Property);
                    if (expectedWt is OmniBufWireType.Sfixed64 or OmniBufWireType.Fixed64)
                    {
                        return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                        {
                            if (wt != expectedWt)
                            {
                                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                                return;
                            }

                            markedSeen = true;
                            set(target, unchecked((long)OmniBufDecoder.ReadFixed64(data, ref pos)));
                        };
                    }

                    if (expectedWt == OmniBufWireType.Uint)
                    {
                        return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                        {
                            if (wt != OmniBufWireType.Uint)
                            {
                                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                                return;
                            }

                            markedSeen = true;
                            set(target, unchecked((long)OmniBufDecoder.ReadVarint(data, ref pos)));
                        };
                    }

                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Sint)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        markedSeen = true;
                        set(target, OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(data, ref pos)));
                    };
                }

            case ScalarKind.Float32:
                {
                    Action<object, float> set = CompileTypedSetter<float>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Float32)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        markedSeen = true;
                        set(target, BitConverter.UInt32BitsToSingle(OmniBufDecoder.ReadFixed32(data, ref pos)));
                    };
                }

            case ScalarKind.Float64:
                {
                    Action<object, double> set = CompileTypedSetter<double>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Float64)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        markedSeen = true;
                        set(target, BitConverter.UInt64BitsToDouble(OmniBufDecoder.ReadFixed64(data, ref pos)));
                    };
                }

            case ScalarKind.String:
                {
                    Action<object, string?> set = CompileTypedSetter<string?>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.String)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        markedSeen = true;
                        set(target, OmniBufDecoder.ReadString(data, ref pos));
                    };
                }

            case ScalarKind.Bytes:
                {
                    Action<object, byte[]?> set = CompileTypedSetter<byte[]?>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool _, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Bytes)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        markedSeen = true;
                        set(target, OmniBufDecoder.ReadLenSlice(data, ref pos, 0).ToArray());
                    };
                }

            default:
                return null;
        }
    }

    private static FastInPlaceDecodeDelegate? BuildFastTypedListDecoder(FieldPlan plan)
    {
        uint fnum = plan.FieldNum;
        if (plan.ElemWireType == OmniBufWireType.SubMsg && plan.ElemType is not null && !plan.ElemType.IsValueType)
        {
            MethodInfo helper = typeof(OmniBufSerializer)
                .GetMethod(nameof(BuildSubMsgListDecoderGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(plan.ElemType);
            return (FastInPlaceDecodeDelegate)helper.Invoke(null, new object[] { plan })!;
        }

        switch (plan.ElemScalar)
        {
            case ScalarKind.String:
                {
                    Func<object, List<string>?> get = CompileTypedGetter<List<string>?>(plan.Property);
                    Action<object, List<string>?> set = CompileTypedSetter<List<string>?>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool alreadySeen, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Array)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        if (depth + 1 > maxDepth)
                        {
                            throw new OmniBufException($"omnibuf: WireArray depth {depth + 1} exceeds maximum {maxDepth}");
                        }

                        ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);
                        if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                            || elemWt != OmniBufWireType.String)
                        {
                            throw new OmniBufException($"omnibuf: invalid WireArray header on field #{fnum}");
                        }

                        markedSeen = true;
                        List<string>? list = get(target);
                        if (list is null)
                        {
                            list = new List<string>(count);
                            set(target, list);
                        }
                        else if (!alreadySeen)
                        {
                            list.Clear();
                        }

                        list.EnsureCapacity(list.Count + count);
                        for (int i = 0; i < count; i++)
                        {
                            list.Add(OmniBufDecoder.ReadString(body, ref p));
                        }

                        if (p != body.Length)
                        {
                            throw new OmniBufException("omnibuf: trailing bytes in WireArray");
                        }
                    };
                }

            case ScalarKind.UInt32 when plan.ElemWireType == OmniBufWireType.Uint:
                {
                    Func<object, List<uint>?> get = CompileTypedGetter<List<uint>?>(plan.Property);
                    Action<object, List<uint>?> set = CompileTypedSetter<List<uint>?>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool alreadySeen, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Array)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        if (depth + 1 > maxDepth)
                        {
                            throw new OmniBufException($"omnibuf: WireArray depth {depth + 1} exceeds maximum {maxDepth}");
                        }

                        ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);
                        if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                            || elemWt != OmniBufWireType.Uint)
                        {
                            throw new OmniBufException($"omnibuf: invalid WireArray header on field #{fnum}");
                        }

                        markedSeen = true;
                        List<uint>? list = get(target);
                        if (list is null)
                        {
                            list = new List<uint>(count);
                            set(target, list);
                        }
                        else if (!alreadySeen)
                        {
                            list.Clear();
                        }

                        list.EnsureCapacity(list.Count + count);
                        for (int i = 0; i < count; i++)
                        {
                            list.Add((uint)OmniBufDecoder.ReadVarint(body, ref p));
                        }

                        if (p != body.Length)
                        {
                            throw new OmniBufException("omnibuf: trailing bytes in WireArray");
                        }
                    };
                }

            case ScalarKind.Int32 when plan.ElemWireType == OmniBufWireType.Sint:
                {
                    Func<object, List<int>?> get = CompileTypedGetter<List<int>?>(plan.Property);
                    Action<object, List<int>?> set = CompileTypedSetter<List<int>?>(plan.Property);
                    return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool alreadySeen, int depth, int maxDepth, ref bool markedSeen) =>
                    {
                        if (wt != OmniBufWireType.Array)
                        {
                            OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                            return;
                        }

                        if (depth + 1 > maxDepth)
                        {
                            throw new OmniBufException($"omnibuf: WireArray depth {depth + 1} exceeds maximum {maxDepth}");
                        }

                        ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);
                        if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                            || elemWt != OmniBufWireType.Sint)
                        {
                            throw new OmniBufException($"omnibuf: invalid WireArray header on field #{fnum}");
                        }

                        markedSeen = true;
                        List<int>? list = get(target);
                        if (list is null)
                        {
                            list = new List<int>(count);
                            set(target, list);
                        }
                        else if (!alreadySeen)
                        {
                            list.Clear();
                        }

                        list.EnsureCapacity(list.Count + count);
                        for (int i = 0; i < count; i++)
                        {
                            list.Add((int)OmniBufConstants.ZigZagDecode64(OmniBufDecoder.ReadVarint(body, ref p)));
                        }

                        if (p != body.Length)
                        {
                            throw new OmniBufException("omnibuf: trailing bytes in WireArray");
                        }
                    };
                }

            default:
                return null;
        }
    }

    private static FastInPlaceDecodeDelegate BuildSubMsgListDecoderGeneric<TElem>(FieldPlan plan)
        where TElem : class
    {
        uint fnum = plan.FieldNum;
        MessageCodec subCodec = plan.SubCodec!;
        Func<object, List<TElem>?> get = CompileTypedGetter<List<TElem>?>(plan.Property);
        Action<object, List<TElem>?> set = CompileTypedSetter<List<TElem>?>(plan.Property);

        return (ReadOnlySpan<byte> data, ref int pos, OmniBufWireType wt, object target, bool alreadySeen, int depth, int maxDepth, ref bool markedSeen) =>
        {
            if (wt != OmniBufWireType.Array)
            {
                OmniBufDecoder.ValidateValue(data, ref pos, wt, depth, maxDepth);
                return;
            }

            if (depth + 1 > maxDepth)
            {
                throw new OmniBufException($"omnibuf: WireArray depth {depth + 1} exceeds maximum {maxDepth}");
            }

            ReadOnlySpan<byte> body = OmniBufDecoder.ReadLenSlice(data, ref pos, 2);
            if (!OmniBufDecoder.TryParseArrayHeader(body, out OmniBufWireType elemWt, out int count, out int p)
                || elemWt != OmniBufWireType.SubMsg)
            {
                throw new OmniBufException($"omnibuf: invalid WireArray header on field #{fnum}");
            }

            markedSeen = true;
            List<TElem>? list = get(target);
            if (list is null)
            {
                list = new List<TElem>(count);
                set(target, list);
            }

            if (!alreadySeen && subCodec.CanDecodeInPlace)
            {
                int prevCount = list.Count;
                if (prevCount > count)
                {
                    list.RemoveRange(count, prevCount - count);
                }
                else
                {
                    list.EnsureCapacity(count);
                }

                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);
                    if (i < list.Count && list[i] is { } existingElem)
                    {
                        subCodec.DecodeInto(sub, existingElem, depth + 2, maxDepth, merge: false);
                    }
                    else if (i < list.Count)
                    {
                        var blank = (TElem)subCodec.CreateBlank();
                        subCodec.DecodeInto(sub, blank, depth + 2, maxDepth, merge: false);
                        list[i] = blank;
                    }
                    else
                    {
                        var blank = (TElem)subCodec.CreateBlank();
                        subCodec.DecodeInto(sub, blank, depth + 2, maxDepth, merge: false);
                        list.Add(blank);
                    }
                }
            }
            else
            {
                if (!alreadySeen)
                {
                    list.Clear();
                }

                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> sub = OmniBufDecoder.ReadLenSlice(body, ref p, 0);
                    list.Add((TElem)subCodec.DecodeNew(sub, depth + 2, maxDepth));
                }
            }

            if (p != body.Length)
            {
                throw new OmniBufException("omnibuf: trailing bytes in WireArray");
            }
        };
    }

    private static Action<object>? BuildFastResetter(FieldPlan plan)
    {
        if (plan.Property.DeclaringType!.IsValueType || plan.Setter is null)
        {
            return null;
        }

        switch (plan.Category)
        {
            case FieldCategory.NullableScalar:
                {
                    Action<object, object?> set = plan.Setter;
                    return target => set(target, null);
                }

            case FieldCategory.Scalar:
                return plan.Scalar switch
                {
                    ScalarKind.Bool => BuildScalarFromResetter(plan.Property, false),
                    ScalarKind.Byte => BuildScalarFromResetter<byte>(plan.Property, 0),
                    ScalarKind.UInt16 => BuildScalarFromResetter<ushort>(plan.Property, 0),
                    ScalarKind.UInt32 => BuildScalarFromResetter(plan.Property, 0u),
                    ScalarKind.UInt64 => BuildScalarFromResetter(plan.Property, 0UL),
                    ScalarKind.SByte => BuildScalarFromResetter<sbyte>(plan.Property, 0),
                    ScalarKind.Int16 => BuildScalarFromResetter<short>(plan.Property, 0),
                    ScalarKind.Int32 => BuildScalarFromResetter(plan.Property, 0),
                    ScalarKind.Int64 => BuildScalarFromResetter(plan.Property, 0L),
                    ScalarKind.Float32 => BuildTypedScalarResetter(plan.Property, 0f),
                    ScalarKind.Float64 => BuildTypedScalarResetter(plan.Property, 0d),
                    ScalarKind.String => BuildTypedScalarResetter<string?>(plan.Property, string.Empty),
                    ScalarKind.Bytes => BuildTypedScalarResetter<byte[]?>(plan.Property, Array.Empty<byte>()),
                    _ => null,
                };

            case FieldCategory.SubMsg when !plan.PropertyType.IsValueType || plan.IsNullableValueType:
                {
                    Action<object, object?> set = plan.Setter;
                    return target => set(target, null);
                }

            case FieldCategory.List:
                {
                    Func<object, object?> get = plan.Getter;
                    Action<object, object?> set = plan.Setter;
                    Func<IList> factory = plan.ListFactory!;
                    return target =>
                    {
                        if (get(target) is IList list)
                        {
                            list.Clear();
                        }
                        else
                        {
                            set(target, factory());
                        }
                    };
                }

            case FieldCategory.Map:
                {
                    Func<object, object?> get = plan.Getter;
                    Action<object, object?> set = plan.Setter;
                    Func<IDictionary> factory = plan.MapFactory!;
                    return target =>
                    {
                        if (get(target) is IDictionary map)
                        {
                            map.Clear();
                        }
                        else
                        {
                            set(target, factory());
                        }
                    };
                }

            default:
                return null;
        }
    }

    private static Action<object> BuildTypedScalarResetter<TProp>(PropertyInfo prop, TProp defaultVal)
    {
        Action<object, TProp> set = CompileTypedSetter<TProp>(prop);
        return target => set(target, defaultVal);
    }

    private static Action<object> BuildScalarFromResetter<TScalar>(PropertyInfo prop, TScalar defaultVal)
    {
        Action<object, TScalar> set = CompileScalarFromSetter<TScalar>(prop);
        return target => set(target, defaultVal);
    }

    private static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (!char.IsUpper(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
