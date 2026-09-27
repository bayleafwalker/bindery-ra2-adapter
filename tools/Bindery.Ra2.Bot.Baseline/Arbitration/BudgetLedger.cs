// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Bot.Baseline.Arbitration;

/// <summary>The four budget pools an intent's <see cref="BudgetShares"/> divides spending into.</summary>
public static class BudgetPools
{
    public const string Economy = "economy";
    public const string Army = "army";
    public const string Tech = "tech";
    public const string Defense = "defense";

    /// <summary>All pools in their fixed order (also the order spending falls back through).</summary>
    public static IReadOnlyList<string> All { get; } = [Economy, Army, Tech, Defense];

    /// <summary>Canonical pool name for a case-insensitive name, or null when it is not a pool.</summary>
    public static string? Normalise(string? pool)
    {
        if (pool is null) return null;
        foreach (string known in All)
        {
            if (string.Equals(known, pool.Trim(), StringComparison.OrdinalIgnoreCase)) return known;
        }
        return null;
    }

    /// <summary>
    /// The pool a purchase of a given role draws from first: economy for harvesters,
    /// MCVs, refineries, power and production buildings; tech for tech buildings and
    /// superweapons; defense for static defenses; army for everything that fights or supports.
    /// </summary>
    public static string ForRole(UnitRole role) => role switch
    {
        UnitRole.Harvester or UnitRole.Mcv or UnitRole.Economy or UnitRole.Power or UnitRole.Production => Economy,
        UnitRole.Tech or UnitRole.Superweapon => Tech,
        UnitRole.Defense => Defense,
        _ => Army,
    };

    public static double ShareOf(BudgetShares shares, string pool)
    {
        ArgumentNullException.ThrowIfNull(shares);
        return pool switch
        {
            Economy => shares.Economy,
            Army => shares.Army,
            Tech => shares.Tech,
            Defense => shares.Defense,
            _ => 0,
        };
    }
}

/// <param name="PlanningPeriodSeconds">
/// Forecast horizon for income. The runtime re-opens the ledger on every
/// operational pass, so the natural horizon is the operational cadence.
/// </param>
/// <param name="AccrueByShare">
/// When false, every period splits the whole capacity by the current shares. When true, each pool is a
/// running account: new credits (capacity above the accounts' total) are split by the shares, spending is
/// charged to the pool it came from, and a pool's cap is its balance. Accrual lets a pool with a small share
/// save up for an item larger than its share of today's credits (a refinery in a 30% economy pool), which a
/// per-period split can never afford once credits fall; the accounts never total more than the capacity.
/// </param>
public sealed record LedgerOptions(double PlanningPeriodSeconds = 1.0, bool AccrueByShare = false)
{
    public static LedgerOptions Default { get; } = new();
}

/// <summary>
/// Credits split into pools by the active intent's shares, reserved per
/// controller before anything is bought (invariant 4). Without a ledger, the
/// production planner and a tactical controller can each "afford" the same
/// credits; with it, reservations plus spending never exceed credits on hand
/// plus one planning period of forecast income.
/// </summary>
/// <remarks>
/// Accounting: <see cref="BeginPeriod"/> fixes <see cref="Capacity"/> =
/// credits + floor(max(0, income per minute) × period / 60) and each pool's cap =
/// floor(capacity × normalised share); it clears all reservations and spending.
/// For every pool, reserved + spent ≤ cap, and the caps sum to at most the
/// capacity, so the invariant holds by construction for any call sequence.
/// Spending draws down a controller's reservation; it can never exceed it.
/// When constructed with an <see cref="ILeaseManager"/>, a controller may reserve
/// from a pool only while it holds that pool's <see cref="LeaseKey.Budget"/> lease,
/// which makes pools single-owner too.
/// </remarks>
public sealed class BudgetLedger
{
    private readonly ILeaseManager? leases;
    private readonly Dictionary<string, int> caps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> spent = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Pool, string Controller), int> reservations = [];
    private readonly Dictionary<string, long> balances = new(StringComparer.Ordinal);

    public BudgetLedger(LedgerOptions? options = null, ILeaseManager? leases = null)
    {
        Options = options ?? LedgerOptions.Default;
        if (!double.IsFinite(Options.PlanningPeriodSeconds) || Options.PlanningPeriodSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Planning period must be finite and non-negative.");
        }
        this.leases = leases;
        foreach (string pool in BudgetPools.All)
        {
            caps[pool] = 0;
            spent[pool] = 0;
            balances[pool] = 0;
        }
    }

    /// <summary>A pool's running account (accrual mode); equals its cap at the start of a period.</summary>
    public long Balance(string pool) => balances[Require(pool)];

    public LedgerOptions Options { get; }

    /// <summary>Start of the current period; lease checks use this time.</summary>
    public GameTime PeriodStart { get; private set; }

    /// <summary>Credits on hand plus forecast income for the period.</summary>
    public int Capacity { get; private set; }

    public int TotalReserved => reservations.Values.Sum();

    public int TotalSpent => spent.Values.Sum();

    /// <summary>
    /// Opens a new planning period: recomputes capacity and pool caps and clears
    /// reservations and spending. Negative credits and income count as zero;
    /// shares that sum to zero or less are split evenly.
    /// </summary>
    public void BeginPeriod(GameTime now, int credits, double incomePerMinute, BudgetShares shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        PeriodStart = now;
        double income = double.IsFinite(incomePerMinute) ? Math.Max(0, incomePerMinute) : 0;
        long capacity = Math.Max(0L, credits) + (long)Math.Floor(income * Options.PlanningPeriodSeconds / 60.0);
        Capacity = (int)Math.Min(int.MaxValue, capacity);

        double[] raw = BudgetPools.All.Select(p => Sanitise(BudgetPools.ShareOf(shares, p))).ToArray();
        double sum = raw.Sum();
        double[] normalised = raw.Select(r => sum > 0 ? r / sum : 1.0 / BudgetPools.All.Count).ToArray();
        if (Options.AccrueByShare)
        {
            Accrue(normalised);
        }
        else
        {
            for (int i = 0; i < BudgetPools.All.Count; i++)
            {
                // The epsilon absorbs binary rounding (1300 × 0.3 must be 390, not 389); caps stay
                // integers whose sum cannot exceed the integer capacity.
                caps[BudgetPools.All[i]] = (int)Math.Floor(Capacity * normalised[i] + 1e-7);
            }
        }
        foreach (string pool in BudgetPools.All) spent[pool] = 0;
        reservations.Clear();
    }

    /// <summary>
    /// Brings the accounts' total to the capacity: a surplus (income) is split by share, the rounding
    /// remainder going to the largest share; a shortfall (credits spent outside the ledger, or a lower
    /// forecast) is taken from every account in proportion to its balance, the remainder from the largest.
    /// </summary>
    private void Accrue(double[] shares)
    {
        IReadOnlyList<string> pools = BudgetPools.All;
        long total = balances.Values.Sum();
        long delta = Capacity - total;
        if (delta > 0)
        {
            long given = 0;
            for (int i = 0; i < pools.Count; i++)
            {
                long part = (long)Math.Floor(delta * shares[i] + 1e-7);
                balances[pools[i]] += part;
                given += part;
            }
            int largest = Array.IndexOf(shares, shares.Max());
            balances[pools[largest]] += delta - given;
        }
        else if (delta < 0)
        {
            long cut = -delta, taken = 0;
            foreach (string pool in pools)
            {
                long part = total > 0 ? (long)Math.Floor(cut * (balances[pool] / (double)total)) : 0;
                part = Math.Min(part, balances[pool]);
                balances[pool] -= part;
                taken += part;
            }
            foreach (string pool in pools.OrderByDescending(p => balances[p]).ThenBy(static p => p, StringComparer.Ordinal))
            {
                if (taken >= cut) break;
                long part = Math.Min(cut - taken, balances[pool]);
                balances[pool] -= part;
                taken += part;
            }
        }
        foreach (string pool in pools) caps[pool] = (int)Math.Min(int.MaxValue, Math.Max(0, balances[pool]));
    }

    public int PoolCapacity(string pool) => caps[Require(pool)];

    public int Reserved(string pool) => reservations.Where(p => p.Key.Pool == Require(pool)).Sum(static p => p.Value);

    public int Reserved(string pool, string controller) =>
        reservations.TryGetValue((Require(pool), controller), out int amount) ? amount : 0;

    public int Spent(string pool) => spent[Require(pool)];

    /// <summary>Credits in a pool neither reserved nor spent.</summary>
    public int Available(string pool)
    {
        string key = Require(pool);
        return Math.Max(0, caps[key] - Reserved(key) - spent[key]);
    }

    /// <summary>Total a controller holds in reservations across all pools.</summary>
    public int ReservedBy(string controller) =>
        reservations.Where(p => string.Equals(p.Key.Controller, controller, StringComparison.Ordinal)).Sum(static p => p.Value);

    /// <summary>True when the controller reserved in any pool this period (even zero), i.e. it is a budgeted controller.</summary>
    public bool HasAccount(string controller) =>
        reservations.Keys.Any(k => string.Equals(k.Controller, controller, StringComparison.Ordinal));

    /// <summary>True when the controller may reserve from the pool (holds its budget lease, when leases are enforced).</summary>
    public bool MayReserve(string pool, string controller) =>
        leases is null || string.Equals(leases.OwnerOf(LeaseKey.Budget(Require(pool)), PeriodStart), controller, StringComparison.Ordinal);

    /// <summary>
    /// Reserves up to <paramref name="amount"/> from a pool and returns what was
    /// granted (possibly less, possibly zero). A zero grant still opens the
    /// controller's account for the period. Returns 0 without an account when
    /// the controller does not hold the pool's lease.
    /// </summary>
    public int Reserve(string pool, string controller, int amount)
    {
        string key = Require(pool);
        ArgumentException.ThrowIfNullOrEmpty(controller);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (!MayReserve(key, controller)) return 0;
        int granted = Math.Min(amount, Available(key));
        reservations[(key, controller)] = Reserved(key, controller) + granted;
        return granted;
    }

    /// <summary>
    /// Reserves up to <paramref name="amount"/> from a pool, and when the pool alone cannot cover it, moves
    /// unreserved balance from the other pools (largest available first, then pool order) into it for the
    /// shortfall. Returns what was granted. Totals are unchanged by the move, so the capacity invariant holds;
    /// the intent's shares still decide who is served first, because the runtime lets every pool reserve from
    /// its own balance before any pool borrows.
    /// </summary>
    public int ReserveWithBorrowing(string pool, string controller, int amount)
    {
        string key = Require(pool);
        ArgumentException.ThrowIfNullOrEmpty(controller);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (!MayReserve(key, controller)) return 0;
        int granted = Reserve(key, controller, amount);
        int shortfall = amount - granted;
        foreach (string other in BudgetPools.All.Where(p => p != key).OrderByDescending(Available).ThenBy(p => BudgetPools.All.ToList().IndexOf(p)))
        {
            if (shortfall <= 0) break;
            int move = Math.Min(shortfall, Available(other));
            if (move <= 0) continue;
            caps[other] -= move;
            caps[key] += move;
            if (Options.AccrueByShare)
            {
                balances[other] -= move;
                balances[key] += move;
            }
            reservations[(key, controller)] = Reserved(key, controller) + move;
            granted += move;
            shortfall -= move;
        }
        return granted;
    }

    /// <summary>All-or-nothing reservation.</summary>
    public bool TryReserve(string pool, string controller, int amount)
    {
        string key = Require(pool);
        ArgumentException.ThrowIfNullOrEmpty(controller);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (!MayReserve(key, controller) || amount > Available(key)) return false;
        reservations[(key, controller)] = Reserved(key, controller) + amount;
        return true;
    }

    /// <summary>Returns a controller's whole reservation in a pool to the pool.</summary>
    public void Release(string pool, string controller) => reservations.Remove((Require(pool), controller));

    /// <summary>Returns part of a controller's reservation in a pool to the pool.</summary>
    public void Release(string pool, string controller, int amount)
    {
        string key = Require(pool);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        int held = Reserved(key, controller);
        if (held == 0 && !reservations.ContainsKey((key, controller))) return;
        reservations[(key, controller)] = Math.Max(0, held - amount);
    }

    /// <summary>Spends from a controller's reservation in one pool; false (and no change) when it is insufficient.</summary>
    public bool Spend(string pool, string controller, int amount)
    {
        string key = Require(pool);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        int held = Reserved(key, controller);
        if (held < amount || !reservations.ContainsKey((key, controller))) return false;
        reservations[(key, controller)] = held - amount;
        spent[key] += amount;
        if (Options.AccrueByShare) balances[key] = Math.Max(0, balances[key] - amount);
        return true;
    }

    /// <summary>
    /// Spends from a controller's reservations, preferred pool first and then the
    /// other pools in <see cref="BudgetPools.All"/> order; all-or-nothing.
    /// </summary>
    public bool TrySpendAny(string controller, string preferredPool, int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        string preferred = Require(preferredPool);
        if (!HasAccount(controller) || ReservedBy(controller) < amount) return false;
        int remaining = amount;
        foreach (string pool in new[] { preferred }.Concat(BudgetPools.All.Where(p => p != preferred)))
        {
            if (remaining == 0) break;
            int take = Math.Min(remaining, Reserved(pool, controller));
            if (take == 0) continue;
            Spend(pool, controller, take);
            remaining -= take;
        }
        return true;
    }

    private static double Sanitise(double share) => double.IsFinite(share) && share > 0 ? share : 0;

    private static string Require(string pool) =>
        BudgetPools.Normalise(pool) ?? throw new ArgumentException($"Unknown budget pool '{pool}'.", nameof(pool));
}
