using FluentAssertions;

namespace Yewnyx.Packables.Test;

public class RegistryTest {
    private sealed class Ship { }

    private sealed class Crew { }

    [Fact]
    public void LooksUpByTypeAndName() {
        var registry = new PackableRegistry.Builder()
            .Add(typeof(Ship), "Owner.Ship", 2, "Ship", "Old.Vessel")
            .Add(typeof(Crew), "Owner.Crew", 0)
            .Build();

        registry.TryGet(typeof(Ship), out var ship).Should().BeTrue();
        (ship!.Name, ship.Version, ship.VersionedName).Should().Be(("Owner.Ship", 2, "Owner.Ship#2"));
        ship.Aliases.Should().Equal("Ship", "Old.Vessel");
        registry.TryGet("Owner.Crew", out var crew).Should().BeTrue();
        crew!.Type.Should().Be(typeof(Crew));

        // Names are looked up without a version, and aliases aren't looked up yet.
        registry.TryGet("Owner.Crew#0", out _).Should().BeFalse();
        registry.TryGet("Ship", out _).Should().BeFalse();
        registry.TryGet(typeof(string), out _).Should().BeFalse();
    }

    [Fact]
    public void BuiltRegistriesDontChange() {
        var builder = new PackableRegistry.Builder().Add(typeof(Ship), "Ship", 0);
        var first = builder.Build();
        builder.Add(typeof(Crew), "Crew", 0);
        var second = builder.Build();

        first.Registrations.Should().HaveCount(1);
        first.TryGet(typeof(Crew), out _).Should().BeFalse();
        second.Registrations.Should().HaveCount(2);
        PackableRegistry.Empty.Registrations.Should().BeEmpty();
    }

    [Fact]
    public void RejectsBadRegistrations() {
        var builder = new PackableRegistry.Builder().Add(typeof(Ship), "Ship", 0);

        var act = () => builder.Add(typeof(Ship), "Other", 0);
        act.Should().Throw<ArgumentException>().WithMessage("*already registered*");
        act = () => builder.Add(typeof(Crew), "Ship", 0);
        act.Should().Throw<ArgumentException>().WithMessage("*registered by both*RegistryTest+Ship*RegistryTest+Crew*");
        act = () => builder.Add(typeof(Crew), "Crew", -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
        act = () => builder.Add(typeof(Crew), "Crew#1", 0);
        act.Should().Throw<ArgumentException>().WithParameterName("name");
        act = () => builder.Add(typeof(Crew), "Crew", 0, "");
        act.Should().Throw<ArgumentException>().WithParameterName("aliases");
        act = () => builder.Add(null!, "Nothing", 0);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AliasesCanHoldOldTypeNames() {
        const string old = "Game.Save.Ship, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
        var registry = new PackableRegistry.Builder().Add(typeof(Ship), "Ship", 0, old, "Tests+Ship").Build();
        registry.Registrations[0].Aliases.Should().Equal(old, "Tests+Ship");
    }

    [Fact]
    public void BuiltListsCantBeChangedByCasting() {
        var registry = new PackableRegistry.Builder().Add(typeof(Ship), "Ship", 0, "Old").Build();
        (registry.Registrations is PackableRegistration[]).Should().BeFalse();
        (registry.Registrations[0].Aliases is string[]).Should().BeFalse();
    }

    [Theory]
    [InlineData("Ship", true)]
    [InlineData("Owner.Ship", true)]
    [InlineData("crate-spawnable", true)]
    [InlineData("Snake_case.9", true)]
    [InlineData("", false)]
    [InlineData("Ship#0", false)]
    [InlineData("t:1", false)]
    [InlineData("Outer+Inner", false)]
    [InlineData("List`1", false)]
    [InlineData("has space", false)]
    [InlineData("é", false)]
    public void NameRules(string name, bool valid) => PackableNames.IsValidName(name).Should().Be(valid);

    [Theory]
    [InlineData("Ship#0", "Ship", 0)]
    [InlineData("Owner.Ship#12", "Owner.Ship", 12)]
    [InlineData("crate-spawnable#2147483647", "crate-spawnable", int.MaxValue)]
    public void ParsesVersionedNames(string text, string name, int version) {
        PackableNames.TryParse(text, out var parsedName, out var parsedVersion).Should().BeTrue();
        (parsedName, parsedVersion).Should().Be((name, version));
        PackableNames.Format(name, version).Should().Be(text);
    }

    [Theory]
    [InlineData("Ship")]
    [InlineData("Ship#")]
    [InlineData("Ship#01")]
    [InlineData("Ship#-1")]
    [InlineData("Ship#2147483648")]
    [InlineData("#3")]
    [InlineData("Sh#ip#3")]
    [InlineData("t:1")]
    [InlineData(null)]
    public void RejectsBadVersionedNames(string? text) => PackableNames.TryParse(text, out _, out _).Should().BeFalse();
}
