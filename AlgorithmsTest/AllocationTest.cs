using FluentAssertions;
using Xunit.Sdk;

namespace Yewnyx.Algorithms.Test;

public class AllocationTest {
    // Allocates in the calls whose number, counted from the first warm-up call, is wanted.
    private static Action Allocating(Func<int, bool> wanted) {
        var call = 0;
        return () => { if (wanted(call++)) { GC.KeepAlive(new byte[64]); } };
    }

    [Fact]
    public void ACodeThatNeverAllocatesPasses() {
        Allocation.AssertNone(Allocating(_ => false));
    }

    [Fact]
    public void AnAllocationInTwoCallsIsLetOff() {
        // Two of the fifty measured calls, which come after the twenty that warm up.
        Allocation.AssertNone(Allocating(call => call == 30 || call == 50));
    }

    [Fact]
    public void AnAllocationInEveryOtherCallFails() {
        var failure = Assert.Throws<XunitException>(() => Allocation.AssertNone(Allocating(call => call % 2 == 0)));
        failure.Message.Should().Contain("25 did");
    }

    [Fact]
    public void AnAllocationInEveryCallFails() {
        Assert.Throws<XunitException>(() => Allocation.AssertNone(Allocating(_ => true)));
    }

    [Fact]
    public void AnAllocationInThreeCallsFails() {
        Assert.Throws<XunitException>(() => Allocation.AssertNone(Allocating(call => call == 30 || call == 40 || call == 50)));
    }

    [Fact]
    public void TheFewestBytesOfCodeThatAllocatesEveryCallIsWhatOneCallAllocates() {
        Allocation.Fewest(Allocating(_ => true)).Should().BeInRange(64, 200);
    }
}
