using Nexus.Service.Lighting;
using Xunit;

namespace Nexus.Service.Tests.Lighting;

public class LcdLockSleepSetTests
{
    [Fact]
    public void Nothing_sleeps_while_unlocked()
    {
        var state = new LcdLockState();
        var set = new LcdLockSleepSet(state);

        Assert.False(set.IsSleeping("a", () => true));
    }

    [Fact]
    public void The_setting_is_read_once_per_lock()
    {
        var state = new LcdLockState();
        var set = new LcdLockSleepSet(state);
        var on = true;
        state.Set(true);

        Assert.True(set.IsSleeping("a", () => on));
        on = false;
        Assert.True(set.IsSleeping("a", () => on));
    }

    [Fact]
    public void A_device_with_the_setting_off_is_decided_awake_for_the_whole_lock()
    {
        var state = new LcdLockState();
        var set = new LcdLockSleepSet(state);
        var on = false;
        state.Set(true);

        Assert.False(set.IsSleeping("a", () => on));
        on = true;
        Assert.False(set.IsSleeping("a", () => on));
    }

    [Fact]
    public void Drain_names_exactly_what_the_lock_slept_and_a_new_lock_decides_afresh()
    {
        var state = new LcdLockState();
        var set = new LcdLockSleepSet(state);
        state.Set(true);
        set.IsSleeping("a", () => true);
        set.IsSleeping("b", () => false);

        state.Set(false);

        Assert.Equal(new[] { "a" }, set.Drain());
        Assert.False(set.IsSleeping("a", () => true));

        state.Set(true);
        Assert.True(set.IsSleeping("b", () => true));
        Assert.Equal(new[] { "b" }, set.Drain());
    }

    [Fact]
    public void Repeated_set_to_the_same_value_raises_nothing()
    {
        var state = new LcdLockState();
        var raised = 0;
        state.Changed += _ => raised++;

        state.Set(true);
        state.Set(true);
        state.Set(false);
        state.Set(false);

        Assert.Equal(2, raised);
    }

    [Fact]
    public void A_throwing_subscriber_does_not_stop_the_others()
    {
        var state = new LcdLockState();
        var seen = false;
        state.Changed += _ => throw new System.InvalidOperationException();
        state.Changed += _ => seen = true;

        state.Set(true);

        Assert.True(state.IsLocked);
        Assert.True(seen);
    }
}
