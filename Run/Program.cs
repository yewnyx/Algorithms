using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Yewnyx.Packables;

var testValue = new Foo {
    x = 42,
    bar = new Bar {
        y = "Hello",
        baz = new Baz {
            z = 100,
            w = "World",
        },
    },
};

var packer = new Packer();
var json = packer.Pack(testValue);
Console.WriteLine(json.ToString(Formatting.Indented));

// ---

struct Foo : IPackable {
    public int x;
    public Bar bar;
    
    public void PackInto(ref Packer packer, JObject storage) {
        storage["x"] = x;
        storage["bar"] = packer.PackReference(bar);
    }

    public void UnpackFrom(ref Packer packer, ObjectId objectId) => throw new NotImplementedException();
}

struct Bar : IPackable {
    public string y;
    public Baz baz;
    public void PackInto(ref Packer packer, JObject storage) {
        storage["y"] = y;
        storage["baz"] = packer.PackReference(baz);
    }

    public void UnpackFrom(ref Packer packer, ObjectId objectId) => throw new NotImplementedException();
}

struct Baz : IPackable {
    public int z;
    public string w;
    
    public void PackInto(ref Packer packer, JObject storage) {
        storage["z"] = z;
        storage["w"] = w;
    }

    public void UnpackFrom(ref Packer packer, ObjectId objectId) => throw new NotImplementedException();
}