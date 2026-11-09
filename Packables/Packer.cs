using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using Yewnyx.Collections;

namespace Yewnyx.Packables;

public ref struct Packer {
    public string ContentType => _contentType;

    private readonly string _contentType;

    private int objectIdInt;
    private int typeIdInt;

    // Objects are told apart by reference, never by Equals: two equal objects are still two objects,
    // and a mutable object's hash can change while it's being packed.
    private Dictionary<IPackable, string> _objectIds;
    private List<IPackable> _objects; // In id order.

    private IReadOnlyTwoWayDictionary<Type, string> _builtInTypes;
    private TwoWayDictionary<Type, string> _explicitTypes;

    // Must stay: for a struct, `new Packer()` never calls the constructor below, even though all of
    // its arguments are optional. Without this one it would leave every field null.
    public Packer() : this(IPackable.DEFAULT_CONTENT_TYPE) { }

    public Packer(string contentType = IPackable.DEFAULT_CONTENT_TYPE,
        IReadOnlyTwoWayDictionary<Type, string>? builtInTypes = null) {
        _contentType = contentType;
        objectIdInt = 0;
        typeIdInt = 0;
        _objectIds = new Dictionary<IPackable, string>(ReferenceComparer.Instance);
        _objects = new List<IPackable>();
        
        if (builtInTypes == null) {
            _builtInTypes = new TwoWayDictionary<Type, string>();
        } else {
            _builtInTypes = new TwoWayDictionary<Type, string>(builtInTypes);
        }

        _explicitTypes = new TwoWayDictionary<Type, string>();
    }

    public JObject Pack(IPackable root) {
        var result = new JObject {
            { "Content-Type", _contentType },
            { "version", IPackable.PACKABLE_VERSION },
        };

        result["root"] = PackReference(root);

        var objs = new JObject();

        // Packing an object can reach new ones, which join the end of _objects, so this runs until
        // nothing new turns up. Objects are packed in id order.
        for (var i = 0; i < _objects.Count; i++) {
            var packable = _objects[i];
            var obj = new JObject();
            packable.PackInto(ref this, obj);

            // Set the packed type (registering if needed) TODO: Fix this
            obj["isa"] = _makeTypeInfo(packable);

            objs[_objectIds[packable]] = obj;
        }

        result["objects"] = objs;

        var typesDict = new JObject();
        foreach (var (type, typeId) in _explicitTypes) {
            var refInfo = new JObject {
                ["type"] = typeId,
                ["fullname"] = type.AssemblyQualifiedName,
                // ["fullname"] = type.FullName,
            };

            typesDict[typeId] = refInfo;
        }

        if (typesDict.Count > 0) { result["types"] = typesDict; }

        return result;
    }

    public ObjectRef PackReference(IPackable? value) {
        if (value is null) { return new ObjectRef("t:null", "o:null"); }

        var objectId = _addObject(value);
        var typeId = _registerTypeId(value);
        return new ObjectRef(typeId, objectId);
    }

    private ObjectId _addObject(IPackable? value) {
        if (value is null) { return new ObjectId("o:null"); }

        if (_objectIds.TryGetValue(value, out var foundId)) { return foundId; }

        objectIdInt++;
        var objectId = (ObjectId)objectIdInt;
        _objectIds[value] = objectId;
        _objects.Add(value);
        return objectId;
    }

    private TypeId _registerTypeId(IPackable? value) {
        if (value is null) { return new TypeId("t:null"); }

        var type = value.GetType();

        if (_builtInTypes.TryGet(type, out var foundImplicitId)) { return foundImplicitId; }

        if (_explicitTypes.TryGet(type, out var foundExplicitId)) { return foundExplicitId; }

        typeIdInt++;
        var typeId = (TypeId)typeIdInt;
        _explicitTypes[type] = typeId;
        return typeId;
    }

    private JToken _makeTypeInfo(IPackable? value, bool extendedInfo = false) {
        if (value is null) { return new TypeId("t:null"); }

        var type = value.GetType();

        var builtIn = _builtInTypes.TryGet(type, out var typeId);
        if (!builtIn) { typeId = _registerTypeId(value); }

        var refInfo = new JObject();
        refInfo["type"] = typeId;
        if (!builtIn && extendedInfo) {
            //refInfo["fullname"] = type.FullName;
            refInfo["fullname"] = type.AssemblyQualifiedName;
        }

        return refInfo;
    }

    // ReferenceEqualityComparer is .NET 5+, so netstandard2.1 needs its own.
    private sealed class ReferenceComparer : IEqualityComparer<IPackable> {
        public static readonly ReferenceComparer Instance = new();
        public bool Equals(IPackable? x, IPackable? y) => ReferenceEquals(x, y);
        public int GetHashCode(IPackable obj) => RuntimeHelpers.GetHashCode(obj);
    }
}