using FluentAssertions;

namespace Yewnyx.Collections.Test;

public class TwoWayDictionaryTest {
    [Fact]
    public void TryAddRejectsAValueThatIsAlreadyMapped() {
        var map = new TwoWayDictionary<string, int> { { "a", 1 } };

        map.TryAdd("b", 1).Should().BeFalse();
        map.Contains("b").Should().BeFalse();
        map.Count.Should().Be(1);
    }

    [Fact]
    public void SettingAKeyForgetsItsOldValue() {
        var map = new TwoWayDictionary<string, int> { { "a", 1 } };

        map["a"] = 2;

        map.Contains(1).Should().BeFalse();
        map[2].Should().Be("a");
    }

    [Fact]
    public void SettingAValueForgetsItsOldKey() {
        var map = new TwoWayDictionary<string, int> { { "a", 1 } };

        map[1] = "b";

        map.Contains("a").Should().BeFalse();
        map["b"].Should().Be(1);
        map.Count.Should().Be(1);
    }

    [Fact]
    public void AddIgnoresDuplicates() {
        var map = new TwoWayDictionary<string, int> { { "a", 1 } };

        map.Add("a", 2);
        map.Add("b", 1);

        map["a"].Should().Be(1);
        map.Count.Should().Be(1);
    }

    [Fact]
    public void AForeachOverItDoesntAllocate() {
        var map = new TwoWayDictionary<string, int>();
        for (var i = 0; i < 10; i++) { map.Add("k" + i, i + 100); }
        long Sum() {
            long sum = 0;
            foreach (var (key, value) in map) { sum += key.Length + value; }
            return sum;
        }

        // The enumerator is the dictionary's own struct, not the interface it'd be boxed to.
        Dictionary<string, int>.Enumerator enumerator = map.GetEnumerator();
        enumerator.Dispose();

        long sum = 0;
        Allocation.Fewest(() => sum = Sum()).Should().Be(0);
        sum.Should().Be(10 * 2 + 1045);
    }

    [Fact]
    public void ItStillEnumeratesThroughTheInterfaces() {
        var map = new TwoWayDictionary<string, int> { { "a", 1 }, { "b", 2 } };

        ((IEnumerable<KeyValuePair<string, int>>)map).Should().BeEquivalentTo(
            new[] { KeyValuePair.Create("a", 1), KeyValuePair.Create("b", 2) });
        var untyped = new List<object>();
        foreach (var pair in (System.Collections.IEnumerable)map) { untyped.Add(pair); }
        untyped.Should().HaveCount(2);
        ((IReadOnlyCollection<KeyValuePair<string, int>>)map).Count.Should().Be(2);
    }

    [Fact]
    public void TryGetFindsEitherWayAndALeavesDefaultOnAMiss() {
        var map = new TwoWayDictionary<string, int> { { "a", 1 } };

        map.TryGet("a", out int value).Should().BeTrue();
        value.Should().Be(1);
        map.TryGet(1, out string key).Should().BeTrue();
        key.Should().Be("a");
        map.TryGet("z", out value).Should().BeFalse();
        value.Should().Be(0);
        map.TryGet(9, out key).Should().BeFalse();
        key.Should().BeNull();
    }

    [Fact]
    public void TheConstructorThrowsForAKeyOrAValueThatComesTwice() {
        KeyValuePair<string, int>[] sameKey = [new("a", 1), new("a", 2)];
        KeyValuePair<string, int>[] sameValue = [new("a", 1), new("b", 1)];

        var byKey = () => new TwoWayDictionary<string, int>(sameKey);
        var byValue = () => new TwoWayDictionary<string, int>(sameValue);

        byKey.Should().Throw<ArgumentException>();
        byValue.Should().Throw<ArgumentException>();
    }
}
