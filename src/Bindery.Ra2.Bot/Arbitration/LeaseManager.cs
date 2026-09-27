// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Arbitration;

/// <summary>
/// Single-owner arbitration over units, squads and budget pools (invariant 4).
/// Two controllers steering one tank is the classic hierarchical-bot failure
/// (orders cancel each other every frame), so a key has at most one live lease,
/// and a stronger claimant can take it only after the holder's minimum hold time.
/// </summary>
/// <remarks>
/// Rules, exactly as <see cref="ILeaseManager"/> documents them:
/// <list type="bullet">
/// <item>A lease is live while <c>now &lt; ExpiresAt</c>; an expired lease is as good as absent.</item>
/// <item>The holder re-acquiring its own key refreshes it: expiry moves to <c>now + ttl</c>, the minimum hold
/// is extended (never shortened) and the priority becomes the requested one.</item>
/// <item>Another owner preempts only with strictly higher priority and only when <c>now &gt;= MinHoldUntil</c>;
/// equal or lower priority never preempts. Preemptions are logged as <c>lease.preempted</c>.</item>
/// <item><see cref="Release"/> by a non-holder is ignored, so a controller cannot free what it does not own.</item>
/// </list>
/// Not thread-safe: all calls come from the runtime's tick thread. Outputs are
/// sorted by key so iteration order never leaks into commands or logs.
/// </remarks>
public sealed class LeaseManager : ILeaseManager
{
    private readonly Dictionary<LeaseKey, Lease> leases = [];
    private readonly IDecisionLog? log;

    public LeaseManager(IDecisionLog? log = null)
    {
        this.log = log;
    }

    /// <summary>Number of stored leases, live or not yet swept.</summary>
    public int Count => leases.Count;

    /// <summary>Preemptions performed since construction.</summary>
    public long Preemptions { get; private set; }

    /// <summary>Snapshot version stamped on preemption records; the runtime sets it each frame.</summary>
    public long SnapshotVersion { get; set; }

    public Lease? TryAcquire(LeaseKey key, string owner, int priority, GameTime now, double minHoldSeconds, double ttlSeconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        if (!double.IsFinite(minHoldSeconds) || minHoldSeconds < 0) throw new ArgumentOutOfRangeException(nameof(minHoldSeconds));
        if (!double.IsFinite(ttlSeconds) || ttlSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(ttlSeconds));

        GameTime expires = now.Plus(ttlSeconds);
        if (expires <= now) expires = new GameTime(now.Frame + 1);
        GameTime minHold = now.Plus(minHoldSeconds);

        if (leases.TryGetValue(key, out Lease? existing) && IsLive(existing, now))
        {
            if (string.Equals(existing.Owner, owner, StringComparison.Ordinal))
            {
                Lease refreshed = existing with
                {
                    Priority = priority,
                    MinHoldUntil = minHold > existing.MinHoldUntil ? minHold : existing.MinHoldUntil,
                    ExpiresAt = expires,
                };
                leases[key] = refreshed;
                return refreshed;
            }
            if (priority <= existing.Priority || now < existing.MinHoldUntil) return null;

            Preemptions++;
            log?.Write(new DecisionRecord(DecisionRecordKinds.LeasePreempted, now, SnapshotVersion, BotJson.ToElement(new
            {
                key = key.Value,
                from = existing.Owner,
                fromPriority = existing.Priority,
                to = owner,
                toPriority = priority,
                heldSinceFrame = existing.Acquired.Frame,
            })));
        }

        Lease lease = new(key, owner, priority, now, minHold, expires);
        leases[key] = lease;
        return lease;
    }

    public bool Renew(LeaseKey key, string owner, GameTime now, double ttlSeconds)
    {
        if (!double.IsFinite(ttlSeconds) || ttlSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(ttlSeconds));
        if (!leases.TryGetValue(key, out Lease? existing) || !IsLive(existing, now)
            || !string.Equals(existing.Owner, owner, StringComparison.Ordinal))
        {
            return false;
        }
        GameTime expires = now.Plus(ttlSeconds);
        leases[key] = existing with { ExpiresAt = expires > now ? expires : new GameTime(now.Frame + 1) };
        return true;
    }

    public void Release(LeaseKey key, string owner)
    {
        if (leases.TryGetValue(key, out Lease? existing) && string.Equals(existing.Owner, owner, StringComparison.Ordinal))
        {
            leases.Remove(key);
        }
    }

    public string? OwnerOf(LeaseKey key, GameTime now) =>
        leases.TryGetValue(key, out Lease? lease) && IsLive(lease, now) ? lease.Owner : null;

    /// <summary>The live lease on a key, or null.</summary>
    public Lease? Get(LeaseKey key, GameTime now) =>
        leases.TryGetValue(key, out Lease? lease) && IsLive(lease, now) ? lease : null;

    public IReadOnlyList<Lease> HeldBy(string owner, GameTime now) =>
        leases.Values
            .Where(l => IsLive(l, now) && string.Equals(l.Owner, owner, StringComparison.Ordinal))
            .OrderBy(static l => l.Key.Value, StringComparer.Ordinal)
            .ToList();

    /// <summary>All live leases, sorted by key.</summary>
    public IReadOnlyList<Lease> Live(GameTime now) =>
        leases.Values.Where(l => IsLive(l, now)).OrderBy(static l => l.Key.Value, StringComparer.Ordinal).ToList();

    /// <summary>Releases every lease an owner holds.</summary>
    public void ReleaseAll(string owner)
    {
        foreach (LeaseKey key in leases.Where(p => string.Equals(p.Value.Owner, owner, StringComparison.Ordinal)).Select(static p => p.Key).ToList())
        {
            leases.Remove(key);
        }
    }

    /// <summary>
    /// Removes expired leases and any lease matching <paramref name="dead"/> (the
    /// runtime passes "unit no longer exists"), returning how many were removed.
    /// </summary>
    public int Sweep(GameTime now, Func<LeaseKey, bool>? dead = null)
    {
        List<LeaseKey> remove = leases
            .Where(p => !IsLive(p.Value, now) || (dead is not null && dead(p.Key)))
            .Select(static p => p.Key)
            .ToList();
        foreach (LeaseKey key in remove) leases.Remove(key);
        return remove.Count;
    }

    private static bool IsLive(Lease lease, GameTime now) => now < lease.ExpiresAt;
}
