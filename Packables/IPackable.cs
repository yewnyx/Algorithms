using System;
using Newtonsoft.Json.Linq;

namespace Yewnyx.Packables;

public interface IPackable {
    void PackInto(ref Packer packer, JObject storage);
    void UnpackFrom(ref Packer packer, ObjectId objectId);
}

public readonly ref struct TypeId {
    public readonly string Id;
    public override string ToString() => Id;
    public TypeId(string id) => Id = id;

    public static implicit operator TypeId(string idStr) => new(idStr);
    public static implicit operator TypeId(int idInt) => new($"t:{idInt}");
    public static implicit operator string(TypeId id) => id.Id;
    public static implicit operator JToken(TypeId id) => id.Id;
}

public readonly ref struct ObjectId {
    public readonly string Id;
    public override string ToString() => Id;
    public ObjectId(string id) => Id = id;
    
    public static implicit operator ObjectId(string idStr) => new(idStr);
    public static implicit operator ObjectId(int idInt) => new($"o:{idInt}");
    public static implicit operator string(ObjectId id) => id.Id;
    public static implicit operator JToken(ObjectId id) => id.Id;
}

public readonly ref struct ObjectRef {
    public readonly TypeId TypeId;
    public readonly ObjectId ObjectId;

    public ObjectRef(TypeId typeId, ObjectId objectId) {
        TypeId = typeId;
        ObjectId = objectId;
    }

    public static implicit operator ObjectRef((string typeId, string objectId) tuple) =>
        new(typeId: tuple.typeId, objectId: tuple.objectId);
    public static implicit operator JToken(ObjectRef objRef) =>
        new JObject {
            {"type", objRef.TypeId},
            {"ref", objRef.ObjectId}
        };
}