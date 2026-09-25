// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Runtime;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arbitration;

public sealed class LeaseManagerTests
{
    private static readonly LeaseKey Tank = LeaseKey.Unit(new EntityId(7));
    private readonly DecisionLog log = new();
    private readonly LeaseManager leases;

    public LeaseManagerTests()
    {
        leases = new LeaseManager(log);
    }

    [Fact]
    public void Free_key_is_granted_and_owned()
    {
        Lease? lease = leases.TryAcquire(Tank, "squad-a", 5, Fx.T(0), 3, 10);
        Assert.NotNull(lease);
        Assert.Equal("squad-a", leases.OwnerOf(Tank, Fx.T(1)));
        Assert.Equal(Fx.T(3), lease!.MinHoldUntil);
        Assert.Equal(Fx.T(10), lease.ExpiresAt);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Lower_or_equal_priority_never_preempts_even_after_min_hold(int priority)
    {
        leases.TryAcquire(Tank, "squad-a", 5, Fx.T(0), 3, 100);
        Assert.Null(leases.TryAcquire(Tank, "harvester-guard", priority, Fx.T(1), 0, 10));
        Assert.Null(leases.TryAcquire(Tank, "harvester-guard", priority, Fx.T(50), 0, 10));
        Assert.Equal("squad-a", leases.OwnerOf(Tank, Fx.T(50)));
        Assert.Empty(log.OfKind(DecisionRecordKinds.LeasePreempted));
    }

    [Fact]
    public void Higher_priority_preempts_only_after_min_hold()
    {
        leases.TryAcquire(Tank, "squad-a", 5, Fx.T(0), 3, 100);

        Assert.Null(leases.TryAcquire(Tank, "retreat", 9, Fx.T(2.9), 0, 10));
        Assert.Equal("squad-a", leases.OwnerOf(Tank, Fx.T(2.9)));

        Lease? taken = leases.TryAcquire(Tank, "retreat", 9, Fx.T(3), 1, 10);
        Assert.NotNull(taken);
        Assert.Equal("retreat", leases.OwnerOf(Tank, Fx.T(3)));
        DecisionRecord record = Assert.Single(log.OfKind(DecisionRecordKinds.LeasePreempted));
        Assert.Equal("squad-a", record.Data.GetProperty("from").GetString());
        Assert.Equal("retreat", record.Data.GetProperty("to").GetString());
        Assert.Equal(1, leases.Preemptions);
    }

    [Fact]
    public void Expired_lease_can_be_taken_by_anyone()
    {
        leases.TryAcquire(Tank, "squad-a", 9, Fx.T(0), 30, 5);
        Assert.Null(leases.OwnerOf(Tank, Fx.T(5)));
        Assert.NotNull(leases.TryAcquire(Tank, "low", 0, Fx.T(5), 0, 5));
        Assert.Equal("low", leases.OwnerOf(Tank, Fx.T(6)));
        Assert.Empty(log.OfKind(DecisionRecordKinds.LeasePreempted));
    }

    [Fact]
    public void Holder_reacquiring_refreshes_expiry_and_never_shortens_min_hold()
    {
        leases.TryAcquire(Tank, "squad-a", 5, Fx.T(0), 10, 5);
        Lease? refreshed = leases.TryAcquire(Tank, "squad-a", 5, Fx.T(4), 1, 5);
        Assert.Equal(Fx.T(10), refreshed!.MinHoldUntil);
        Assert.Equal(Fx.T(9), refreshed.ExpiresAt);
        Assert.Equal(Fx.T(0), refreshed.Acquired);
    }

    [Fact]
    public void Only_the_holder_can_renew_or_release()
    {
        leases.TryAcquire(Tank, "squad-a", 5, Fx.T(0), 0, 5);
        Assert.False(leases.Renew(Tank, "intruder", Fx.T(1), 50));
        leases.Release(Tank, "intruder");
        Assert.Equal("squad-a", leases.OwnerOf(Tank, Fx.T(1)));

        Assert.True(leases.Renew(Tank, "squad-a", Fx.T(4), 50));
        Assert.Equal("squad-a", leases.OwnerOf(Tank, Fx.T(40)));
        leases.Release(Tank, "squad-a");
        Assert.Null(leases.OwnerOf(Tank, Fx.T(41)));
    }

    [Fact]
    public void Renewing_an_expired_lease_fails()
    {
        leases.TryAcquire(Tank, "squad-a", 5, Fx.T(0), 0, 5);
        Assert.False(leases.Renew(Tank, "squad-a", Fx.T(6), 5));
    }

    [Fact]
    public void Held_by_is_sorted_and_live_only_and_sweep_removes_dead_keys()
    {
        leases.TryAcquire(LeaseKey.Unit(new EntityId(9)), "squad-a", 1, Fx.T(0), 0, 10);
        leases.TryAcquire(LeaseKey.Unit(new EntityId(2)), "squad-a", 1, Fx.T(0), 0, 10);
        leases.TryAcquire(LeaseKey.Squad("s1"), "squad-a", 1, Fx.T(0), 0, 1);
        Assert.Equal(["unit:2", "unit:9"], leases.HeldBy("squad-a", Fx.T(2)).Select(l => l.Key.Value).ToArray());

        int removed = leases.Sweep(Fx.T(2), k => k == LeaseKey.Unit(new EntityId(9)));
        Assert.Equal(2, removed);
        Assert.Equal(["unit:2"], leases.HeldBy("squad-a", Fx.T(2)).Select(l => l.Key.Value).ToArray());
    }

    [Fact]
    public void Invalid_ttl_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => leases.TryAcquire(Tank, "a", 1, Fx.T(0), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => leases.TryAcquire(Tank, "a", 1, Fx.T(0), -1, 5));
    }
}
