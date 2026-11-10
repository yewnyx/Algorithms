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
}
