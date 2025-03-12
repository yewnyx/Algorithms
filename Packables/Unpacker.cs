using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Yewnyx.Collections;

namespace Yewnyx.Packables;

public ref struct Unpacker {
    public string ContentType => _contentType;

    private readonly string _contentType;

    private JObject _document;
    private JObject _objects;

    private TwoWayDictionary<string, IPackable> _objectsDict;

    private IReadOnlyTwoWayDictionary<Type, string> _builtInTypes;
    private TwoWayDictionary<Type, string> _explicitTypes;

    public Unpacker(JObject document) : this(IPackable.DEFAULT_CONTENT_TYPE, document) { }

    public Unpacker(string contentType, JObject document,
        IReadOnlyTwoWayDictionary<Type, string>? builtInTypes = null) {
        _contentType = contentType;
        _document = document;
        _objectsDict = new();

        if (builtInTypes == null) { _builtInTypes = new TwoWayDictionary<Type, string>(); } else {
            _builtInTypes = new TwoWayDictionary<Type, string>(builtInTypes);
        }

        _explicitTypes = new TwoWayDictionary<Type, string>();
    }

    public bool TryUnpack<TPackable>(ref TPackable packable) where TPackable : class, IPackable =>
        TryUnpack(false, ref packable);

    public bool TryUnpack<TPackable>(bool lenientContentType, ref TPackable packable)
        where TPackable : class, IPackable {
        if (_document == null) { return false; }

        if (!lenientContentType) {
            // Strict content-type check
            if (!_document.TryGetValue("Content-Type", out var contentTypeToken)) { return false; }

            if (contentTypeToken.ToString() != _contentType) { return false; }
        }

        // Doesn't have { ... "root": { ... } }
        if (!_document.TryGetValue("root", out var rootToken)) { return false; }

        var rootObjectRef = (ObjectRef)rootToken;

        // "root" is not an ObjectRef
        if (rootObjectRef is { IsNull: true }) { return false; }

        // No "objects" key
        if (!_document.TryGetValue("objects", out var objectsToken)) { return false; }

        // "objects" value is not a JObject
        if (objectsToken is not JObject objectsObj) { return false; }

        _objects = objectsObj;

        var rootObjectId = rootObjectRef.ObjectId;

        // Root object is not in the objects list
        if (!objectsObj.TryGetValue(rootObjectId, out var rootObject)) { return false; }

        if (_document.TryGetValue("types", out var typesToken) && typesToken is JObject typesObj) {
            _loadTypes(typesObj);
        }

        _objectsDict[rootObjectId] = packable;
        packable.UnpackFrom(ref this, rootObjectId);

        return false;
    }

    public bool TryCreateFromReference<TPackable>(JToken token, out TPackable? packable, Func<Type, TPackable?> factory)
        where TPackable : class, IPackable {
        packable = null;

        // No or invalid JSON to unpack
        if (token is not JObject objectRefObject) { return false; }

        var objectRef = (ObjectRef)objectRefObject;

        // No object
        if (objectRef.ObjectId.IsNull) { return false; }

        var objectId = objectRef.ObjectId;

        // No type
        if (objectRef.TypeId.IsNull) { return false; }

        var typeId = objectRef.TypeId;

        // Type doesn't resolve
        if (!_tryResolveTypeId(typeId, out var type, out _)) { return false; }

        // Add the raw packable first.
        // If it needed to be added, create then unpack. Else it was already there.
        if (!_objectsDict.Contains(objectId)) {
            packable = factory(type);
            if (packable == null) { return false; }

            _objectsDict[objectId] = packable;
            packable.UnpackFrom(ref this, objectId);
            return true;
        }

        return false;
    }

    public bool TryGetJson(string key, ObjectId objectId, out JToken result) {
        result = null;

        // Get the object's dictionary
        if (!_objects.TryGetValue(objectId, out var objectToken)) { return false; }

        if (objectToken is not JObject jsonObject) {
            Console.Error.WriteLine($"Object '{objectId.ToString()}' is not a JObject.");
            return false;
        }

        return jsonObject.TryGetValue(key, out result);
    }


    private bool _tryResolveTypeId(string typeId, out Type type, out bool isBuiltIn) {
        if (_builtInTypes.TryGet(typeId, out type)) {
            isBuiltIn = true;
            return true;
        }

        if (_explicitTypes.TryGet(typeId, out type)) {
            isBuiltIn = false;
            return true;
        }

        isBuiltIn = false;
        return false;
    }

    private void _loadTypes(JObject types) {
        if (types.Count == 0) { return; }

        foreach (var typeProperty in types.Properties()) {
            var typeListKey = typeProperty.Name;
            if (_tryResolveTypeId(typeListKey, out _, out _)) { continue; }

            Type type = null;

            if (typeProperty.Value is JObject typeObj && typeObj.TryGetValue("fullname", out var typeNameToken)) {
                var typeName = typeNameToken.ToString();
                if (_tryResolveTypeId(typeName, out type, out var isBuiltIn)) {
                    if (!isBuiltIn) {
                        if (!_explicitTypes.TryAdd(type, typeListKey)) {
                            Console.Error.WriteLine(
                                $"Could not register type '{typeListKey}' as '{type.FullName}'.");
                        }
                    }

                    continue;
                }

                type = Type.GetType(typeName);
                if (type == null) {
                    Console.Error.WriteLine($"Did not find type for: '{typeObj.ToString(Formatting.None)}'.");
                    continue;
                }
            }

            if (!_explicitTypes.Contains(type)) { _explicitTypes[typeListKey] = type; }
        }
    }
}