using System;
using Newtonsoft.Json.Linq;

namespace Yewnyx.Packables;

public interface IPackable {
    public const string DEFAULT_CONTENT_TYPE = "application/packable+json";
    public const int PACKABLE_VERSION = 1;

    void PackInto(ref Packer packer, JObject storage);
    void UnpackFrom(ref Unpacker unpacker, ObjectId objectId);
}

public readonly ref struct TypeId {
    public readonly string Id;
    public override string ToString() => Id;
    public bool IsNull => Id == "t:null";
    public TypeId(string? id) => Id = string.IsNullOrWhiteSpace(id) ? "t:null" : id;
    public TypeId() : this(null) { }

    public static implicit operator TypeId(string? idStr) => new(idStr);
    public static implicit operator TypeId(int idInt) => new($"t:{idInt}");
    public static implicit operator string(TypeId id) => id.Id;
    public static implicit operator JToken(TypeId id) => id.Id;

    public static explicit operator TypeId(JToken? token) {
        if (token == null) { return new TypeId(); }

        return token.ToObject<string>();
    }
}

public readonly ref struct ObjectId {
    public readonly string Id;
    public override string ToString() => Id;
    public bool IsNull => Id == "o:null";
    public ObjectId(string? id) => Id = string.IsNullOrWhiteSpace(id) ? "o:null" : id;
    public ObjectId() : this(null) { }

    public static implicit operator ObjectId(string? idStr) => new(idStr);
    public static implicit operator ObjectId(int idInt) => new($"o:{idInt}");
    public static implicit operator string(ObjectId id) => id.Id;
    public static implicit operator JToken(ObjectId id) => id.Id;

    public static explicit operator ObjectId(JToken? token) {
        if (token == null) { return new ObjectId(); }

        return token.ToObject<string>();
    }
}

public readonly ref struct ObjectRef {
    public readonly TypeId TypeId;

    public readonly ObjectId ObjectId;

    // No ToString() implementation, to avoid accidental stringification
    public bool IsNull => TypeId.IsNull && ObjectId.IsNull;

    public ObjectRef(TypeId typeId, ObjectId objectId) {
        TypeId = typeId;
        ObjectId = objectId;
    }

    public ObjectRef() : this(new TypeId(), new ObjectId()) { }

    public static implicit operator ObjectRef((string typeId, string objectId) tuple) =>
        new(typeId: tuple.typeId, objectId: tuple.objectId);

    public static implicit operator JToken(ObjectRef objRef) =>
        new JObject {
            { "type", objRef.TypeId },
            { "ref", objRef.ObjectId }
        };

    public static explicit operator ObjectRef(JToken? token) {
        if (token is not JObject obj) { return new ObjectRef(); }

        if (!obj.TryGetValue("type", out var typeToken)) { return new ObjectRef(); }

        if (!obj.TryGetValue("ref", out var refToken)) { return new ObjectRef(); }

        TypeId typeId = typeToken.ToObject<string>();
        ObjectId objectId = refToken.ToObject<string>();

        if (typeId.IsNull || objectId.IsNull) { return new ObjectRef(); }

        return new(typeId, objectId);
    }
}