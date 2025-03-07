using FluentAssertions.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Yewnyx.Packables.Test;

public class PackablesTests {
    
    private readonly ITestOutputHelper _output;

    public PackablesTests(ITestOutputHelper output) { _output = output; }

    [Fact]
    public void ShouldPackCorrectly() {
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
        var actual = packer.Pack(testValue);
        // _output.WriteLine(actual.ToString(Formatting.None));
        var expected = JToken.Parse(
            @"{""version"":1,""root"":{""type"":""t:1"",""ref"":""o:1""},""objects"":{""o:1"":{""x"":42,""bar"":{""type"":""t:2"",""ref"":""o:2""},""isa"":{""type"":""t:1""}},""o:2"":{""y"":""Hello"",""baz"":{""type"":""t:3"",""ref"":""o:3""},""isa"":{""type"":""t:2""}},""o:3"":{""z"":100,""w"":""World"",""isa"":{""type"":""t:3""}}},""types"":{""t:1"":{""type"":""t:1"",""fullname"":""Yewnyx.Packables.Test.PackablesTests+Foo, PackablesTest, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null""},""t:2"":{""type"":""t:2"",""fullname"":""Yewnyx.Packables.Test.PackablesTests+Bar, PackablesTest, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null""},""t:3"":{""type"":""t:3"",""fullname"":""Yewnyx.Packables.Test.PackablesTests+Baz, PackablesTest, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null""}}}");
        actual.Should().BeEquivalentTo(expected);
    }

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
}