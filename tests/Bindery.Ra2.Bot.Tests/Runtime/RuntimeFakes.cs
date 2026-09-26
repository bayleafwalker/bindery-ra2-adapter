// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Tests.Arbitration;

namespace Bindery.Ra2.Bot.Tests.Runtime;

internal static class Proposals
{
    public static StrategistProposal For(StrategistContext context, string id, string playbookId, StrategicPosture posture, double confidence = 0.6, double lifetime = 120) =>
        new(Fx.Intent(id, playbookId, posture, issuedAt: context.Features.Time.Seconds, lifetime: lifetime, confidence: confidence,
                version: context.Features.SnapshotVersion),
            new ProposalCost(0, 0, 0, 0, null), null);
}

/// <summary>Answers synchronously from a function of the context.</summary>
internal sealed class ScriptedStrategist(string id, IntentSource source, Func<StrategistContext, StrategistProposal?> answer) : IStrategist
{
    public List<StrategistContext> Contexts { get; } = [];

    public string Id => id;

    public IntentSource Source => source;

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        Contexts.Add(context);
        return Task.FromResult(answer(context));
    }

    public static ScriptedStrategist Fallback() =>
        new("fallback", IntentSource.Fallback, c => Proposals.For(c, $"fb-{c.Features.SnapshotVersion}", "generic-defend", StrategicPosture.Defend, 0.5));
}

/// <summary>Returns a task the test completes by hand.</summary>
internal sealed class ControlledStrategist(IntentSource source = IntentSource.Llm) : IStrategist
{
    public List<(StrategistContext Context, TaskCompletionSource<StrategistProposal?> Completion, CancellationToken Token)> Calls { get; } = [];

    public string Id => "controlled";

    public IntentSource Source => source;

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<StrategistProposal?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Calls.Add((context, completion, cancellationToken));
        return completion.Task;
    }

    public void CompleteLast(string id, string playbookId, StrategicPosture posture, double confidence = 0.6)
    {
        var call = Calls[^1];
        call.Completion.SetResult(Proposals.For(call.Context, id, playbookId, posture, confidence));
    }
}

/// <summary>Never answers, and ignores cancellation, like a hung network call.</summary>
internal sealed class NeverStrategist : IStrategist
{
    public List<CancellationToken> Tokens { get; } = [];

    public string Id => "never";

    public IntentSource Source => IntentSource.Llm;

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        Tokens.Add(cancellationToken);
        return new TaskCompletionSource<StrategistProposal?>().Task;
    }
}

internal sealed class ThrowingStrategist(bool synchronous) : IStrategist
{
    public string Id => "throwing";

    public IntentSource Source => IntentSource.Llm;

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        if (synchronous) throw new InvalidOperationException("boom-sync");
        return Task.FromException<StrategistProposal?>(new InvalidOperationException("boom-async"));
    }
}

/// <summary>A deterministic strategist that computes on the thread pool (answers after a short wall-clock delay).</summary>
internal sealed class ThreadPoolStrategist : IStrategist
{
    public string Id => "threadpool";

    public IntentSource Source => IntentSource.Selector;

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            await Task.Delay(30, CancellationToken.None).ConfigureAwait(false);
            return (StrategistProposal?)Proposals.For(context, $"tp-{context.Features.SnapshotVersion}", "allied-boom", StrategicPosture.Boom);
        }, CancellationToken.None);
}

/// <summary>
/// LLM stand-in with deterministic latency: answers <see cref="DelayFrames"/> frames after the request,
/// alternating playbooks by request count.
/// </summary>
/// <summary>Like the Claude strategist with the arena's failure subscription: logs its own failure detail, then answers null.</summary>
internal sealed class SelfLoggingFailingStrategist(IDecisionLog log) : IStrategist
{
    private int calls;

    public string Id => "self-logging";

    public IntentSource Source => IntentSource.Llm;

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        calls++;
        if (calls % 2 == 1)
        {
            log.Write(new DecisionRecord(DecisionRecordKinds.ProposalFailed, context.Features.Time, context.Features.SnapshotVersion,
                BotJson.ToElement(new { strategist = Id, code = "claude.parse_failed", detail = $"call {calls}" })));
            return Task.FromResult<StrategistProposal?>(null);
        }
        return Task.FromResult<StrategistProposal?>(new StrategistProposal(
            Fx.Intent($"self-{calls}", "allied-boom", StrategicPosture.Boom, issuedAt: context.Features.Time.Seconds, source: IntentSource.Llm, version: context.Features.SnapshotVersion),
            new ProposalCost(0, 10, 10, 0, "fake-model"),
            null));
    }
}

internal sealed class DelayedStrategist(int delayFrames) : IStrategist, IFrameAwareStrategist
{
    private readonly List<(long Due, TaskCompletionSource<StrategistProposal?> Completion, StrategistProposal? Proposal)> pending = [];
    private int calls;

    public int DelayFrames => delayFrames;

    public string Id => "delayed-llm";

    public IntentSource Source => IntentSource.Llm;

    public Task<StrategistProposal?> ProposeAsync(StrategistContext context, CancellationToken cancellationToken = default)
    {
        calls++;
        StrategistProposal? proposal = calls % 3 == 0
            ? null
            : new StrategistProposal(
                Fx.Intent($"llm-{calls}", calls % 2 == 0 ? "allied-turtle" : "allied-boom",
                    calls % 2 == 0 ? StrategicPosture.Turtle : StrategicPosture.Boom,
                    issuedAt: context.Features.Time.Seconds, confidence: 0.55 + 0.1 * (calls % 4), source: IntentSource.Llm,
                    version: context.Features.SnapshotVersion,
                    parameters: new Dictionary<string, double> { ["expandAt"] = 3, ["aggression"] = 0.25 * calls }),
                new ProposalCost(1.25 * calls, 1000 + calls, 200, 50, "fake-model"),
                $"{{\"call\":{calls}}}");
        TaskCompletionSource<StrategistProposal?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add((context.Features.Time.Frame + delayFrames, completion, proposal));
        return completion.Task;
    }

    public void OnFrame(GameTime now)
    {
        foreach (var item in pending.Where(p => p.Due <= now.Frame).ToList())
        {
            item.Completion.TrySetResult(item.Proposal);
            pending.Remove(item);
        }
    }
}

/// <summary>Belief: own entities straight from the frame, version = frame count.</summary>
internal sealed class FakeBeliefModel : IBeliefModel
{
    private long version;

    public BeliefSnapshot Current { get; private set; } = Fx.Belief(0, 0);

    public BeliefSnapshot Apply(ObservationFrame frame)
    {
        version++;
        List<OwnEntity> own = frame.Entities
            .Where(e => e.Owner == frame.Self)
            .Select(e => new OwnEntity(e.Id, e.TypeId, Fx.Rules.Get(e.TypeId).Role, Fx.Rules.Get(e.TypeId).Kind, e.Position,
                frame.Map.RegionOf(e.Position)!.Id, e.HealthFraction, Fx.Rules.Get(e.TypeId).Cost, e.Deployed))
            .ToList();
        Current = new BeliefSnapshot(version, frame.Time, frame.Mode, frame.Self, frame.Faction, frame.Credits, frame.Power, own,
            [], [], frame.Queues, new Dictionary<RegionId, GameTime>(), frame.Events, frame.Map) { QueuesKnown = frame.QueuesKnown };
        return Current;
    }
}

internal sealed class FakeFeatureCompiler(Func<GameTime, IReadOnlyList<StrategicEvent>>? events = null) : IFeatureCompiler
{
    public StrategicFeatures Compile(BeliefSnapshot snapshot)
    {
        StrategicFeatures features = Fx.Features(snapshot.Time.Seconds, snapshot.Version, snapshot.Faction,
            ownArmy: snapshot.Own.Where(e => e.Kind != EntityKind.Building).Sum(e => e.Value),
            events: events?.Invoke(snapshot.Time) ?? [],
            credits: snapshot.Credits);
        return features;
    }
}

/// <summary>Two tanks per plan, one squad of units 2 and 3; issues production as "ops".</summary>
internal sealed class FakePlanner : IOperationalPlanner
{
    public List<GameTime> Calls { get; } = [];

    public bool Throw { get; set; }

    public IReadOnlyDictionary<string, int> Reservations { get; set; } = new Dictionary<string, int>();

    public int UnreportedDebt { get; set; }

    public OperationalPlan Plan(BeliefSnapshot belief, StrategicFeatures features, StrategicIntent intent, ILeaseManager leases)
    {
        Calls.Add(belief.Time);
        if (Throw) throw new InvalidOperationException("planner broke");
        EntityId[] squadUnits = belief.Own.Where(e => e.Id.Value is 2 or 3).Select(e => e.Id).ToArray();
        return new OperationalPlan(
            belief.Time,
            intent.IntentId,
            [new ProduceCommand("ops", "mtnk", QueueKind.Vehicle), new ProduceCommand("ops", "mtnk", QueueKind.Vehicle)],
            [new SquadOrder("s1", ObjectiveKind.AttackRegion, Fx.R1, squadUnits, true, 0.6)],
            Reservations,
            [$"intent {intent.PlaybookId}"],
            UnreportedDebt);
    }
}

/// <summary>Leases its squads' units and attack-moves them.</summary>
internal sealed class FakeSquadController : ITacticalController
{
    public int Calls { get; private set; }

    public string Id => "squad";

    public IReadOnlyList<GameCommand> Tick(BeliefSnapshot belief, IReadOnlyList<SquadOrder> squads, ILeaseManager leases, IRulesDatabase rules)
    {
        Calls++;
        List<GameCommand> commands = [];
        foreach (SquadOrder squad in squads)
        {
            List<EntityId> mine = squad.Units
                .Where(u => leases.TryAcquire(LeaseKey.Unit(u), Id, 5, belief.Time, 5, 2) is not null)
                .ToList();
            if (mine.Count > 0) commands.Add(new AttackMoveCommand(Id, mine, new Cell(30, 30)));
        }
        return commands;
    }
}

/// <summary>Orders unit 4 around without leasing it.</summary>
internal sealed class RogueController : ITacticalController
{
    public string Id => "rogue";

    public IReadOnlyList<GameCommand> Tick(BeliefSnapshot belief, IReadOnlyList<SquadOrder> squads, ILeaseManager leases, IRulesDatabase rules) =>
        belief.Own.Any(e => e.Id.Value == 4) ? [new MoveCommand(Id, [new EntityId(4)], new Cell(0, 0))] : [];
}

internal static class Frames
{
    public static ObservationFrame At(long frame, int credits = 1000, bool includeUnit2 = true)
    {
        PlayerId self = new(1);
        List<ObservedEntity> entities =
        [
            new(new EntityId(1), self, "gacnst", new Cell(5, 5), 1000, 1000, true),
            new(new EntityId(3), self, "mtnk", new Cell(6, 6), 400, 400),
            new(new EntityId(4), self, "mtnk", new Cell(7, 7), 400, 400),
            new(new EntityId(90), new PlayerId(2), "htnk", new Cell(50, 50), 400, 400),
        ];
        if (includeUnit2) entities.Insert(1, new ObservedEntity(new EntityId(2), self, "mtnk", new Cell(6, 5), 400, 400));
        return new ObservationFrame(new GameTime(frame), ObservationMode.Belief, self, Faction.Allied, credits, new PowerState(100, 50),
            entities, [], [], new HashSet<RegionId> { Fx.R0 }, Fx.Map);
    }
}

internal sealed class ListSource(IEnumerable<ObservationFrame> frames) : IObservationSource
{
    private readonly Queue<ObservationFrame> queue = new(frames);

    public ObservationMode Mode => ObservationMode.Belief;

    public ValueTask<ObservationFrame?> NextAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(queue.Count > 0 ? queue.Dequeue() : null);
}

internal sealed class ListSink : ICommandSink
{
    public List<GameCommand> Commands { get; } = [];

    public void Submit(GameCommand command) => Commands.Add(command);
}

internal static class Runtimes
{
    public static BotRuntime Create(
        IStrategist primary,
        IStrategist? fallback = null,
        IStrategist? shadow = null,
        BotOptions? options = null,
        FakePlanner? planner = null,
        IReadOnlyList<ITacticalController>? tactics = null,
        IDecisionLog? log = null,
        Func<GameTime, IReadOnlyList<StrategicEvent>>? events = null) =>
        new(new BotComponents(
            new FakeBeliefModel(),
            new FakeFeatureCompiler(events),
            Fx.Rules,
            Fx.Playbooks,
            new IntentValidator(),
            planner ?? new FakePlanner(),
            tactics ?? [new FakeSquadController(), new RogueController()],
            primary,
            fallback ?? ScriptedStrategist.Fallback(),
            shadow,
            log ?? new DecisionLog(),
            options ?? new BotOptions(RunDeterministicStrategistsInline: true)));
}
