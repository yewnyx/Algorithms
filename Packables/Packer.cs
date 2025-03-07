using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Yewnyx.Collections;

namespace Yewnyx.Packables;

public ref struct Packer
{
    public const int PACKABLE_VERSION = 1;

    private IReadOnlyTwoWayDictionary<Type, string> builtInTypes;
    private TwoWayDictionary<Type, string> explicitTypes;
    private TwoWayDictionary<string, IPackable> objects;
    private int objectIdInt;
    private int typeIdInt;

    public Packer() {
        builtInTypes = new TwoWayDictionary<Type, string>();
        explicitTypes = new TwoWayDictionary<Type, string>();
        objects = new TwoWayDictionary<string, IPackable>();
        objectIdInt = 0;
        typeIdInt = 0;
    }
    
    public JObject Pack(IPackable root) {
        var result = new JObject {{"version", PACKABLE_VERSION}};

        result["root"] = PackReference(root);

        var alreadySeen = new HashSet<IPackable>();
        var objs = new JObject();

        const int recursionLimit = 8;
        for (var i = 0; i < recursionLimit; i++) {
            var snapshot = objects.ToArray();
            foreach (var (_, packable) in snapshot) {
                // Skip already-packed objects
                if (alreadySeen.Contains(packable)) { continue; }
                
                alreadySeen.Add(packable);

                // Pack the object
                var obj = new JObject();
                var packableId = _addObject(packable);
                packable.PackInto(ref this, obj);

                // Set the packed type (registering if needed)
                obj["isa"] = _makeTypeInfo(packable);

                // Attach the packed object to the refs dict
                objs[packableId] = obj;
            }
        }

        result["objects"] = objs;
        
        var typesDict = new JObject();
        foreach (var (type, typeId) in explicitTypes) {
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
        if (value is null) {
            return new ObjectRef("t:null", "o:null");
        }
        var objectId = _addObject(value);
        var typeId = _registerTypeId(value);
        return new ObjectRef(typeId, objectId);
    }

    private ObjectId _addObject(IPackable? value) {
        if (value is null) { return new ObjectId("o:null"); }
        if (objects.TryGet(value, out var foundId)) {
            return foundId;
        }

        objectIdInt++;
        var objectId = (ObjectId)objectIdInt;
        objects[objectId] = value;
        return objectId;
    }

    private TypeId _registerTypeId(IPackable? value) {
        if (value is null) { return new TypeId("t:null"); }
        var type = value.GetType();

        if (builtInTypes.TryGet(type, out var foundImplicitId)) { return foundImplicitId; }
        if (explicitTypes.TryGet(type, out var foundExplicitId)) { return foundExplicitId; }

        typeIdInt++;
        var typeId = (TypeId)typeIdInt;
        explicitTypes[type] = typeId;
        return typeId;
    }
    
    private JToken _makeTypeInfo(IPackable? value, bool extendedInfo = false) {
        if (value is null) { return new TypeId("t:null"); }
        var type = value.GetType();

        var builtIn = builtInTypes.TryGet(type, out var typeId);
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