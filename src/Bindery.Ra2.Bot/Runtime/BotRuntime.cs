// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Operations;

namespace Bindery.Ra2.Bot.Runtime;

/// <summary>
/// One bot, one player. <see cref="Tick"/> runs a single game frame through every
/// layer in the spec's order — belief, features, strategy lifecycle (never
/// blocking), validation and arbitration, operations on their cadence, tactics,
/// the command gate — and returns only commands that passed the gate. The same
/// instance serves the simulator, retail RA2 and replays; only the components differ.
/// </summary>
/// <remarks>
/// Ownership conventions the runtime enforces around the components:
/// <list type="bullet">
/// <item>Unit leases on units that no longer exist in belief are swept every frame, so a dead unit's id can never
/// block or authorise anything.</item>
/// <item>Budget pool leases belong to the production controller: on every operational pass the runtime gives all
/// four pool leases to it, opens a new ledger period from credits, income and the intent's shares, and reserves the
/// plan's <see cref="OperationalPlan.BudgetReservations"/> (keys are pool names, case-insensitive). A plan without
/// reservations is granted each pool's full share. The production controller is the single
/// <see cref="GameCommand.Controller"/> of the plan's production commands, or
/// <see cref="BotOptions.OperationsControllerId"/> when they name none or several.</item>
/// <item>Production commands are issued once, on the frame their plan is made; squad orders persist until the next plan.</item>
/// <item>A throwing planner or tactical controller is contained and logged (<c>operations.failed</c>,
/// <c>tactics.failed</c>): one bad component must not stop the bot, and the decision log keeps the evidence.</item>
/// </list>
/// Not thread-safe: call <see cref="Tick"/> from one thread.
/// </remarks>
public sealed class BotRuntime : IDisposable
{
    private const int BudgetLeasePriority = 1000;

    private readonly BotComponents components;
    private readonly BotOptions options;
    private readonly IDecisionLog log;
    private IReadOnlyList<SquadOrder> squads = [];
    private GameTime? lastPlanTime;
    private string? lastPlanIntentId;
    private int lastPlanPhase;
    private GameTime lastOperationalEventTime = new(long.MinValue);
    private long? lastTacticalFrame;

    public BotRuntime(BotComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(components.Belief);
        ArgumentNullException.ThrowIfNull(components.Features);
        ArgumentNullException.ThrowIfNull(components.Rules);
        ArgumentNullException.ThrowIfNull(components.Playbooks);
        ArgumentNullException.ThrowIfNull(components.Validator);
        ArgumentNullException.ThrowIfNull(components.Planner);
        ArgumentNullException.ThrowIfNull(components.Tactics);
        ArgumentNullException.ThrowIfNull(components.Primary);
        ArgumentNullException.ThrowIfNull(components.Fallback);
        ArgumentNullException.ThrowIfNull(components.Log);
        ArgumentNullException.ThrowIfNull(components.Options);
        components.Options.Validate();

        this.components = components;
        options = components.Options;
        log = components.Log;
        Metrics = new BotMetrics();
        Leases = new LeaseManager(log);
        Ledger = new BudgetLedger(new LedgerOptions(options.OperationalCadenceSeconds, options.AccrueBudgets), Leases);
        Gate = new CommandGate(Leases, Ledger, components.Rules, log, Metrics);
        Arbiter = new IntentArbiter(components.Playbooks, options.Arbiter, log, Metrics);
        Scheduler = new StrategyScheduler(
            components.Primary, components.Fallback, components.Shadow, components.Validator, Arbiter,
            components.Rules, components.Playbooks, log, options.ToSchedulerOptions(), Metrics);
    }

    public BotOptions Options => options;

    public BotMetrics Metrics { get; }

    public LeaseManager Leases { get; }

    public BudgetLedger Ledger { get; }

    public CommandGate Gate { get; }

    public IntentArbiter Arbiter { get; }

    public StrategyScheduler Scheduler { get; }

    public IDecisionLog Log => log;

    public StrategicIntent? ActiveIntent => Arbiter.Active;

    public BeliefSnapshot? CurrentBelief { get; private set; }

    public StrategicFeatures? CurrentFeatures { get; private set; }

    public OperationalPlan? LastPlan { get; private set; }

    /// <summary>What a strategist asked now would receive, built from <see cref="CurrentFeatures"/>; null before the first frame.</summary>
    public StrategistContext? CurrentStrategistContext => CurrentFeatures is null ? null : Scheduler.ContextFor(CurrentFeatures, CurrentBelief);

    /// <summary>Squad orders currently handed to tactics.</summary>
    public IReadOnlyList<SquadOrder> Squads => squads;

    /// <summary>Commands the gate dropped on the most recent frame.</summary>
    public IReadOnlyList<DroppedCommand> LastDropped { get; private set; } = [];

    public IReadOnlyList<GameCommand> Tick(ObservationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        BeliefSnapshot belief = components.Belief.Apply(frame);
        StrategicFeatures features = components.Features.Compile(belief);
        CurrentBelief = belief;
        CurrentFeatures = features;
        GameTime now = belief.Time;
        Metrics.Frames++;

        Leases.SnapshotVersion = belief.Version;
        HashSet<LeaseKey> liveUnits = belief.Own.Select(static e => LeaseKey.Unit(e.Id)).ToHashSet();
        Leases.Sweep(now, key => key.Value.StartsWith("unit:", StringComparison.Ordinal) && !liveUnits.Contains(key));

        SchedulerTickReport report = Scheduler.Tick(belief, features);

        List<GameCommand> commands = [];
        StrategicIntent? intent = Arbiter.EffectiveIntent;
        bool operationalEvent = NewOperationalEvent(features);
        if (intent is not null
            && (lastPlanTime is null
                || report.IntentChanged
                || !string.Equals(lastPlanIntentId, intent.IntentId, StringComparison.Ordinal)
                || lastPlanPhase != Arbiter.PhaseIndex
                || operationalEvent
                || now.SecondsSince(lastPlanTime.Value) >= options.OperationalCadenceSeconds))
        {
            commands.AddRange(RunOperations(belief, features, intent));
        }

        if (lastTacticalFrame is null || now.Frame - lastTacticalFrame.Value >= options.TacticalPeriodFrames)
        {
            lastTacticalFrame = now.Frame;
            foreach (ITacticalController controller in components.Tactics)
            {
                try
                {
                    commands.AddRange(controller.Tick(belief, squads, Leases, components.Rules) ?? []);
                }
                catch (Exception ex)
                {
                    Metrics.ComponentFailures++;
                    log.Write(new DecisionRecord(RuntimeRecordKinds.TacticsFailed, now, belief.Version, BotJson.ToElement(new
                    {
                        controller = controller.Id,
                        error = ex.GetType().Name,
                        message = ex.Message,
                    })));
                }
            }
        }

        GateResult gated = Gate.Filter(commands, now, belief.Version);
        LastDropped = gated.Dropped;
        return gated.Passed;
    }

    /// <summary>
    /// Drives the bot from a frame source to a command sink until the source ends
    /// (returns null) or cancellation. Returns the number of frames processed.
    /// </summary>
    public async Task<long> RunAsync(IObservationSource source, ICommandSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        long frames = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ObservationFrame? frame = await source.NextAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null) break;
            foreach (GameCommand command in Tick(frame)) sink.Submit(command);
            frames++;
        }
        return frames;
    }

    public void Dispose() => Scheduler.Dispose();

    private bool NewOperationalEvent(StrategicFeatures features)
    {
        bool fresh = false;
        foreach (StrategicEvent evt in features.Events)
        {
            if (evt.Severity >= options.OperationalEventSeverity && evt.Time > lastOperationalEventTime)
            {
                fresh = true;
            }
        }
        if (fresh)
        {
            lastOperationalEventTime = features.Events
                .Where(e => e.Severity >= options.OperationalEventSeverity)
                .Max(static e => e.Time);
        }
        return fresh;
    }

    private IReadOnlyList<GameCommand> RunOperations(BeliefSnapshot belief, StrategicFeatures features, StrategicIntent intent)
    {
        GameTime now = belief.Time;
        lastPlanTime = now;
        lastPlanIntentId = intent.IntentId;
        lastPlanPhase = Arbiter.PhaseIndex;
        OperationalPlan plan;
        try
        {
            plan = components.Planner.Plan(belief, features, intent, Leases);
        }
        catch (Exception ex)
        {
            Metrics.ComponentFailures++;
            log.Write(new DecisionRecord(RuntimeRecordKinds.PlanFailed, now, belief.Version, BotJson.ToElement(new
            {
                intentId = intent.IntentId,
                error = ex.GetType().Name,
                message = ex.Message,
            })));
            return [];
        }

        IReadOnlyList<GameCommand> production = plan.ProductionCommands ?? [];
        List<string> controllers = production.Select(static c => c.Controller).Distinct(StringComparer.Ordinal).ToList();
        string controller = controllers.Count == 1 ? controllers[0] : options.OperationsControllerId;

        double ttl = Math.Max(options.OperationalCadenceSeconds * 2, 1.0 / GameTime.FramesPerSecond);
        foreach (string pool in BudgetPools.All)
        {
            LeaseKey key = LeaseKey.Budget(pool);
            string? owner = Leases.OwnerOf(key, now);
            if (owner is not null && !string.Equals(owner, controller, StringComparison.Ordinal)) Leases.Release(key, owner);
            Leases.TryAcquire(key, controller, BudgetLeasePriority, now, 0, ttl);
        }
        // Reported queues say what they owe; unreported ones (retail RA2) only the planner's own orders can.
        int unpaid = !options.ProductionChargedWhileBuilding ? 0
            : belief.QueuesKnown ? UnpaidProduction(belief)
            : Math.Max(0, plan.UnreportedProductionDebt);
        Ledger.BeginPeriod(now, belief.Credits - unpaid, features.Economy.IncomePerMinute.Current, intent.Budget);

        List<object> reservations = [];
        List<string> unknownPools = [];
        IReadOnlyDictionary<string, int> requested = plan.BudgetReservations ?? new Dictionary<string, int>();
        if (requested.Count == 0)
        {
            foreach (string pool in BudgetPools.All)
            {
                int amount = Ledger.PoolCapacity(pool);
                reservations.Add(new { pool, requested = amount, granted = Ledger.Reserve(pool, controller, amount) });
            }
        }
        else
        {
            // Two passes: every pool first reserves from its own share, then pools still short borrow what the
            // others left unreserved, largest share first, so the intent's split decides who waits.
            Dictionary<string, (int Requested, int Granted)> grants = new(StringComparer.Ordinal);
            foreach (string key in requested.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                string? pool = BudgetPools.Normalise(key);
                if (pool is null)
                {
                    unknownPools.Add(key);
                    continue;
                }
                int amount = Math.Max(0, requested[key]);
                grants[pool] = (amount, Ledger.Reserve(pool, controller, amount));
            }
            foreach (string pool in grants.Keys
                .OrderByDescending(p => BudgetPools.ShareOf(intent.Budget, p))
                .ThenBy(static p => p, StringComparer.Ordinal)
                .ToList())
            {
                (int amount, int granted) = grants[pool];
                if (granted < amount) grants[pool] = (amount, granted + Ledger.ReserveWithBorrowing(pool, controller, amount - granted));
            }
            foreach ((string pool, (int amount, int granted)) in grants.OrderBy(static g => g.Key, StringComparer.Ordinal))
            {
                reservations.Add(new { pool, requested = amount, granted });
            }
        }

        squads = plan.Squads ?? [];
        LastPlan = plan;
        Metrics.Plans++;
        if (options.LogPlans)
        {
            log.Write(new DecisionRecord(DecisionRecordKinds.Plan, now, belief.Version, BotJson.ToElement(new
            {
                intentId = intent.IntentId,
                controller,
                capacity = Ledger.Capacity,
                unpaid,
                reservations,
                unknownPools,
                production = production.Select(static c => new { controller = c.Controller, command = c.GetType().Name }).ToList(),
                squads = squads.Select(static s => new
                {
                    squadId = s.SquadId,
                    objective = s.Objective,
                    targetRegion = s.TargetRegion.Value,
                    units = s.Units.Count,
                    engage = s.Engage,
                }).ToList(),
                notes = plan.Notes ?? [],
            })));
        }
        return production;
    }

    /// <summary>
    /// What queued production still owes (<see cref="ProductionDebt.Unpaid"/>). Where the game charges while
    /// building, the credits on hand do not yet show these commitments, so a ledger sized from credits alone would
    /// fund them again every period and spread the money over more queues than it can pay for (invariant 4).
    /// </summary>
    private int UnpaidProduction(BeliefSnapshot belief) => ProductionDebt.Unpaid(components.Rules, belief.Queues);
}
