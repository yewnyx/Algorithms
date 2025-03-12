using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Yewnyx.Collections;

namespace Yewnyx.Packables;

public ref struct Packer {
    public string ContentType => _contentType;

    private readonly string _contentType;

    private int objectIdInt;
    private int typeIdInt;

    private TwoWayDictionary<string, IPackable> _objectsDict;

    private IReadOnlyTwoWayDictionary<Type, string> _builtInTypes;
    private TwoWayDictionary<Type, string> _explicitTypes;

    public Packer() : this(null) { }

    public Packer(IReadOnlyTwoWayDictionary<Type, string>? builtInTypes) : this(IPackable.DEFAULT_CONTENT_TYPE,
        builtInTypes) { }

    public Packer(string contentType, IReadOnlyTwoWayDictionary<Type, string>? builtInTypes) {
        _contentType = contentType;
        objectIdInt = 0;
        typeIdInt = 0;
        _objectsDict = new TwoWayDictionary<string, IPackable>();
        
        if (builtInTypes == null) {
            _builtInTypes = new TwoWayDictionary<Type, string>();
        } else {
            _builtInTypes = new TwoWayDictionary<Type, string>(builtInTypes);
        }

        _explicitTypes = new TwoWayDictionary<Type, string>();
    }

    public JObject Pack(IPackable root) {
        var result = new JObject {
            { "version", IPackable.PACKABLE_VERSION },
        };

        result["root"] = PackReference(root);

        var alreadySeen = new HashSet<IPackable>();
        var objs = new JObject();

        const int recursionLimit = 8;
        for (var i = 0; i < recursionLimit; i++) {
            var snapshot = _objectsDict.ToArray();
            foreach (var (_, packable) in snapshot) {
                // Skip already-packed objects
                if (alreadySeen.Contains(packable)) { continue; }

                alreadySeen.Add(packable);

                // Pack the object
                var obj = new JObject();
                var packableId = _addObject(packable);
                packable.PackInto(ref this, obj);

                // Set the packed type (registering if needed) TODO: Fix this
                obj["isa"] = _makeTypeInfo(packable);

                // Attach the packed object to the refs dict
                objs[packableId] = obj;
            }
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

        if (_objectsDict.TryGet(value, out var foundId)) { return foundId; }

        objectIdInt++;
        var objectId = (ObjectId)objectIdInt;
        _objectsDict[objectId] = value;
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
}