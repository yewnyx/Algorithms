using FluentAssertions;
using FluentAssertions.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using Yewnyx.Collections;

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
    
    
    [Fact]
    public void ShouldUnpackCorrectly() {
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
        var packed = packer.Pack(testValue);
        var unpacker = new Unpacker(packed);
        var unpacked = new Foo();
        unpacker.TryUnpack(true, ref unpacked).Should();
        var repacker = new Packer();
        var repacked = repacker.Pack(unpacked);
        
        packed.Should().BeEquivalentTo(repacked);
        
        _output.WriteLine(repacked.ToString(Formatting.None));
        // var expected = JToken.Parse(
        //     @"{""version"":1,""root"":{""type"":""t:1"",""ref"":""o:1""},""objects"":{""o:1"":{""x"":42,""bar"":{""type"":""t:2"",""ref"":""o:2""},""isa"":{""type"":""t:1""}},""o:2"":{""y"":""Hello"",""baz"":{""type"":""t:3"",""ref"":""o:3""},""isa"":{""type"":""t:2""}},""o:3"":{""z"":100,""w"":""World"",""isa"":{""type"":""t:3""}}},""types"":{""t:1"":{""type"":""t:1"",""fullname"":""Yewnyx.Packables.Test.PackablesTests+Foo, PackablesTest, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null""},""t:2"":{""type"":""t:2"",""fullname"":""Yewnyx.Packables.Test.PackablesTests+Bar, PackablesTest, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null""},""t:3"":{""type"":""t:3"",""fullname"":""Yewnyx.Packables.Test.PackablesTests+Baz, PackablesTest, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null""}}}");
        
    }
    
    [Fact]
    public void ShouldHandleBuiltInTypes() {
        var testValue = new Foo {
            x = 69,
            bar = new Bar {
                y = "Nice",
            },
        };
    
        const string fooType =
            "Yewnyx.Packables.Test.PackablesTests+Foo, PackablesTest, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        const string barType =
            "Yewnyx.Packables.Test.PackablesTests+Bar, PackablesTest, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        var builtInTypes = new TwoWayDictionary<Type, string> {
            { typeof(Foo), fooType },
            { typeof(Bar), barType },
        }; 
    
        var packer = new Packer(builtInTypes);
        var actual = packer.Pack(testValue);
        _output.WriteLine(actual.ToString(Formatting.Indented));
    }

    sealed class Foo : IPackable, IEquatable<Foo>
    {
        public int x;
        public Bar bar;

        public void PackInto(ref Packer packer, JObject storage) {
            storage["x"] = x;
            storage["bar"] = packer.PackReference(bar);
        }

        public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
            if (unpacker.TryGetJson("x", objectId, out var xToken)) {
                x = xToken.ToObject<int>();
            }
            
            if (unpacker.TryGetJson("bar", objectId, out var barToken)) {
                unpacker.TryCreateFromReference(barToken, out bar, type => new Bar());
            }
        }

        public bool Equals(Foo? other) {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return x == other.x && bar.Equals(other.bar);
        }

        public override bool Equals(object? obj) => 
            ReferenceEquals(this, obj) || obj is Foo other && Equals(other);

        public override int GetHashCode() => 
            HashCode.Combine(x, bar);

        public static bool operator ==(Foo? left, Foo? right) => 
            Equals(left, right);

        public static bool operator !=(Foo? left, Foo? right) => 
            !Equals(left, right);
    }

    sealed class Bar : IPackable, IEquatable<Bar> {
        public string y;
        public Baz baz;

        public void PackInto(ref Packer packer, JObject storage) {
            storage["y"] = y;
            if (baz != null) {
                storage["baz"] = packer.PackReference(baz); 
            }
        }

        public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
            if (unpacker.TryGetJson("y", objectId, out var yToken)) {
                y = yToken.ToObject<string>() ?? string.Empty;
            }
            if (unpacker.TryGetJson("baz", objectId, out var bazToken)) {
                unpacker.TryCreateFromReference(bazToken, out baz, type => new Baz());
            }
        }

        public bool Equals(Bar? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return y == other.y && baz.Equals(other.baz);
        }

        public override bool Equals(object? obj) => 
            ReferenceEquals(this, obj) || obj is Bar other && Equals(other);

        public override int GetHashCode() => 
            HashCode.Combine(y, baz);

        public static bool operator ==(Bar? left, Bar? right) => 
            Equals(left, right);

        public static bool operator !=(Bar? left, Bar? right) => 
            !Equals(left, right);
    }

    sealed class Baz : IPackable, IEquatable<Baz> {
        public int z;
        public string w;

        public void PackInto(ref Packer packer, JObject storage) {
            storage["z"] = z;
            storage["w"] = w;
        }

        public void UnpackFrom(ref Unpacker unpacker, ObjectId objectId) {
            if (unpacker.TryGetJson("z", objectId, out var zToken)) {
                z = zToken.ToObject<int>();
            }
            if (unpacker.TryGetJson("w", objectId, out var wToken)) {
                w = wToken.ToObject<string>() ?? string.Empty;
            }
        }

        public bool Equals(Baz? other) {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return z == other.z && w == other.w;
        }

        public override bool Equals(object? obj) => 
            ReferenceEquals(this, obj) || obj is Baz other && Equals(other);

        public override int GetHashCode() => 
            HashCode.Combine(z, w);

        public static bool operator ==(Baz? left, Baz? right) => 
            Equals(left, right);

        public static bool operator !=(Baz? left, Baz? right) => 
            !Equals(left, right);
    }
}