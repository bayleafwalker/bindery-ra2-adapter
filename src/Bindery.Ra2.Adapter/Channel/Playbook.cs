// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Bindery.Ra2.Adapter.Channel;

/// <summary>
/// The payload field names the player view reads. The bridge owns the
/// payload shape; these defaults are the convention the filter already uses
/// (<c>house</c>) plus the obvious names for the rest. Configure them to
/// match what the bridge actually emits.
/// </summary>
public sealed record PayloadFields(
    string House = "house",
    string Credits = "credits",
    string Type = "type",
    string Object = "object");

/// <summary>A compact account of what one house knows, for triggers and planners.</summary>
public sealed record PlayerViewSummary(
    string House,
    TimeSpan Elapsed,
    long? Credits,
    long? CreditsChangeOverWindow,
    IReadOnlyDictionary<string, int> OwnUnits,
    IReadOnlyList<string> OwnBuildingTypes,
    int EnemySightings,
    IReadOnlyList<string> DefeatedHouses);

/// <summary>
/// What one house has been allowed to see so far, built only from
/// observations its <see cref="PlayerObservationFilter"/> admitted. It never
/// sees more than the filter does; it only remembers.
/// </summary>
public sealed class PlayerView
{
    private readonly List<(DateTimeOffset At, long Credits)> credits = [];
    private readonly Dictionary<string, int> ownUnits = new(StringComparer.Ordinal);
    private readonly SortedSet<string> ownBuildings = new(StringComparer.Ordinal);
    private readonly SortedSet<string> defeated = new(StringComparer.Ordinal);

    public PlayerView(string house, PayloadFields? fields = null, TimeSpan? economyWindow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(house);
        House = house;
        Fields = fields ?? new PayloadFields();
        EconomyWindow = economyWindow ?? TimeSpan.FromSeconds(90);
    }

    public string House { get; }

    public PayloadFields Fields { get; }

    public TimeSpan EconomyWindow { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? LastAt { get; private set; }

    public int EnemySightings { get; private set; }

    public long? Credits => credits.Count == 0 ? null : credits[^1].Credits;

    /// <summary>Credit change across the economy window, once the window is covered.</summary>
    public long? CreditsChangeOverWindow
    {
        get
        {
            if (credits.Count < 2) return null;
            (DateTimeOffset lastAt, long last) = credits[^1];
            (DateTimeOffset firstAt, long first) = credits[0];
            return lastAt - firstAt >= EconomyWindow ? last - first : null;
        }
    }

    /// <summary>Whether the observation was about another house's object -- something this house can see of its enemies.</summary>
    public bool IsForeign(RawObservation observation) =>
        Owner(observation) is { } owner && !string.Equals(owner, House, StringComparison.Ordinal);

    public bool IsOwn(RawObservation observation) =>
        string.Equals(Owner(observation), House, StringComparison.Ordinal);

    public string? Owner(RawObservation observation) => Read(observation.Payload, Fields.House);

    public string? TypeOf(RawObservation observation) => Read(observation.Payload, Fields.Type);

    public void Apply(RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        StartedAt ??= observation.ReceivedAt;
        LastAt = observation.ReceivedAt;
        bool own = IsOwn(observation);
        if (IsForeign(observation) && observation.EventType is Ra2TelemetryEventTypes.UnitCreated or Ra2TelemetryEventTypes.BuildingPlaced or Ra2TelemetryEventTypes.OrderIssued)
            EnemySightings++;
        switch (observation.EventType)
        {
            case Ra2TelemetryEventTypes.CreditsSampled when own && ReadLong(observation.Payload, Fields.Credits) is { } value:
                credits.Add((observation.ReceivedAt, value));
                // Keep just enough history to cover the window.
                while (credits.Count > 2 && observation.ReceivedAt - credits[1].At >= EconomyWindow) credits.RemoveAt(0);
                break;
            case Ra2TelemetryEventTypes.UnitCreated when own && TypeOf(observation) is { } type:
                ownUnits[type] = ownUnits.GetValueOrDefault(type) + 1;
                break;
            case Ra2TelemetryEventTypes.UnitDestroyed or Ra2TelemetryEventTypes.UnitKilled when own && TypeOf(observation) is { } type:
                if (ownUnits.TryGetValue(type, out int count)) ownUnits[type] = Math.Max(0, count - 1);
                break;
            case Ra2TelemetryEventTypes.BuildingPlaced when own && TypeOf(observation) is { } type:
                ownBuildings.Add(type);
                break;
            case Ra2TelemetryEventTypes.PlayerDefeated when Owner(observation) is { } house:
                defeated.Add(house);
                break;
        }
    }

    public PlayerViewSummary Summarize() => new(
        House,
        StartedAt is { } start && LastAt is { } last ? last - start : TimeSpan.Zero,
        Credits,
        CreditsChangeOverWindow,
        ownUnits.Where(static pair => pair.Value > 0).OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
        ownBuildings.ToArray(),
        EnemySightings,
        defeated.ToArray());

    private static string? Read(JsonElement payload, string field) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(field, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement payload, string field) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(field, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? number
            : null;
}

/// <summary>Names a meaningful moment -- one worth revising the plan for.</summary>
public interface IPlaybookTrigger
{
    /// <summary>The trigger's name if this observation fires it, after the view has applied it.</summary>
    string? Detect(PlayerView view, RawObservation observation);
}

/// <summary>The match started: plan the opening.</summary>
public sealed class OpeningTrigger : IPlaybookTrigger
{
    public const string Name = "opening";

    public string? Detect(PlayerView view, RawObservation observation) =>
        observation.EventType == Ra2TelemetryEventTypes.MatchStarted ? Name : null;
}

/// <summary>
/// An enemy object came into view, at most once per cooldown. Only objects
/// the filter admitted count, so this is what the house could see, not what
/// a spectator could.
/// </summary>
public sealed class NewThreatTrigger(TimeSpan? cooldown = null) : IPlaybookTrigger
{
    public const string Name = "new_threat";

    private readonly TimeSpan cooldown = cooldown ?? TimeSpan.FromSeconds(60);
    private DateTimeOffset? last;

    public string? Detect(PlayerView view, RawObservation observation)
    {
        if (!view.IsForeign(observation) || observation.EventType is not (Ra2TelemetryEventTypes.UnitCreated or Ra2TelemetryEventTypes.BuildingPlaced or Ra2TelemetryEventTypes.OrderIssued))
            return null;
        if (last is { } previous && observation.ReceivedAt - previous < cooldown) return null;
        last = observation.ReceivedAt;
        return Name;
    }
}

/// <summary>
/// Credits have not grown by <paramref name="minimumGain"/> across the view's
/// economy window. Fires once, then re-arms after the economy recovers.
/// </summary>
public sealed class StalledEconomyTrigger(long minimumGain = 0) : IPlaybookTrigger
{
    public const string Name = "stalled_economy";

    private bool fired;

    public string? Detect(PlayerView view, RawObservation observation)
    {
        if (observation.EventType != Ra2TelemetryEventTypes.CreditsSampled || !view.IsOwn(observation)) return null;
        if (view.CreditsChangeOverWindow is not { } change) return null;
        if (change > minimumGain)
        {
            fired = false;
            return null;
        }
        if (fired) return null;
        fired = true;
        return Name;
    }
}

/// <summary>
/// The house placed a building that opens a new tier. The defaults are the
/// Yuri's Revenge battle-lab type IDs as the rules name them; verify against
/// the ruleset in use and pass your own list if it differs.
/// </summary>
public sealed class TechTransitionTrigger(IEnumerable<string>? techBuildings = null) : IPlaybookTrigger
{
    public const string Name = "tech_transition";

    private readonly HashSet<string> tech = new(techBuildings ?? ["GATECH", "NATECH", "YATECH"], StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

    public string? Detect(PlayerView view, RawObservation observation) =>
        observation.EventType == Ra2TelemetryEventTypes.BuildingPlaced
        && view.IsOwn(observation)
        && view.TypeOf(observation) is { } type
        && tech.Contains(type)
        && seen.Add(type)
            ? Name
            : null;
}

/// <summary>The current plan: numbered, human-readable, and machine-usable through its directives.</summary>
public sealed record Playbook(int Revision, string Text, IReadOnlyDictionary<string, string> Directives)
{
    public static Playbook Empty { get; } = new(0, "no plan yet", new Dictionary<string, string>(StringComparer.Ordinal));

    public string Directive(string key, string fallback = "") => Directives.TryGetValue(key, out string? value) ? value : fallback;
}

public sealed record PlanningRequest(string Trigger, Playbook Current, PlayerViewSummary View);

/// <summary>
/// Revises the playbook at a trigger. It may be slow -- a model call, a
/// search -- so the controller keeps playing the current playbook until the
/// new one arrives. Do slow work asynchronously: the controller calls
/// <see cref="PlanAsync"/> on the observation path.
/// </summary>
public interface IPlaybookPlanner
{
    string PlannerId { get; }

    Task<Playbook> PlanAsync(PlanningRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A fixed trigger-to-directive table. Deterministic, so it is the baseline
/// any smarter planner is compared against across matches with the same
/// map and seed.
/// </summary>
public sealed class RulePlaybookPlanner : IPlaybookPlanner
{
    public string PlannerId => "rules/v1";

    public Task<Playbook> PlanAsync(PlanningRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Dictionary<string, string> directives = new(request.Current.Directives, StringComparer.Ordinal);
        switch (request.Trigger)
        {
            case OpeningTrigger.Name:
                directives["economy"] = "build_refineries";
                directives["posture"] = "scout";
                directives["tech"] = "hold";
                break;
            case NewThreatTrigger.Name:
                directives["posture"] = "defend";
                break;
            case StalledEconomyTrigger.Name:
                directives["economy"] = "expand";
                break;
            case TechTransitionTrigger.Name:
                directives["tech"] = "advance";
                directives["posture"] = "pressure";
                break;
        }
        string text = string.Join(", ", directives.OrderBy(static d => d.Key, StringComparer.Ordinal).Select(static d => $"{d.Key}={d.Value}"));
        return Task.FromResult(new Playbook(request.Current.Revision + 1, text, directives));
    }
}

/// <summary>
/// Turns the current playbook into routine orders. Deterministic and cheap:
/// it runs on every admitted observation.
/// </summary>
public interface IRoutineController
{
    IReadOnlyList<PlayerCommand> Decide(Playbook playbook, PlayerView view, RawObservation observation);
}

/// <summary>
/// Issues nothing: the seat plans and traces but does not act. The default,
/// and what every seat without a live command sink should use.
/// </summary>
public sealed class IdleRoutineController : IRoutineController
{
    public IReadOnlyList<PlayerCommand> Decide(Playbook playbook, PlayerView view, RawObservation observation) => [];
}

/// <summary>
/// The first real routine: once the match has started, deploy the house's
/// own opening MCV, the first one its view reports, exactly once.
/// </summary>
/// <remarks>
/// The MCV is found from the filtered stream (<c>ra2.unit.created</c> for the
/// seat's own house, with the unit's <see cref="PayloadFields.Object"/>
/// address), which the live source emits for every starting unit in the
/// first in-game snapshot. The defaults are the Yuri's Revenge MCV type IDs
/// as the rules name them; verify against the ruleset in use. A deploy the
/// game rejects is traced as a failed command and not retried.
/// </remarks>
public sealed class DeployMcvRoutineController(IEnumerable<string>? mcvTypes = null) : IRoutineController
{
    private bool deployed;

    public IReadOnlyCollection<string> McvTypes { get; } = new HashSet<string>(mcvTypes ?? ["AMCV", "SMCV", "PCV"], StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PlayerCommand> Decide(Playbook playbook, PlayerView view, RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(observation);
        if (deployed
            || view.StartedAt is null
            || observation.EventType != Ra2TelemetryEventTypes.UnitCreated
            || !view.IsOwn(observation)
            || view.TypeOf(observation) is not { } type
            || !McvTypes.Contains(type)
            || !observation.Payload.TryGetProperty(view.Fields.Object, out JsonElement address)
            || !address.TryGetUInt32(out uint mcv))
            return [];
        deployed = true;
        return [new PlayerCommand(PlayerCommandKinds.Deploy, JsonSerializer.SerializeToElement(new { objects = new[] { mcv } }))];
    }
}

/// <summary>
/// The two-speed controller: a planner revises the playbook at meaningful
/// events, and a deterministic routine controller issues orders from
/// whichever playbook is current.
/// </summary>
/// <remarks>
/// At most one plan is in flight. A trigger that fires while one is in
/// flight, or inside <c>minimumPlanInterval</c> of the last one, is noted
/// and dropped rather than queued: a stale plan for an old threat is worse
/// than none. Cooldowns run on observation time, so replaying a recording
/// makes the same decisions.
/// </remarks>
public sealed class PlaybookController : IPlayerController
{
    private readonly IPlaybookPlanner planner;
    private readonly IRoutineController routine;
    private readonly IReadOnlyList<IPlaybookTrigger> triggers;
    private readonly TimeSpan minimumPlanInterval;
    private Task<Playbook>? inFlight;
    private string? inFlightTrigger;
    private DateTimeOffset? lastPlanAt;

    public PlaybookController(
        string house,
        IPlaybookPlanner planner,
        IRoutineController? routine = null,
        IReadOnlyList<IPlaybookTrigger>? triggers = null,
        PayloadFields? fields = null,
        TimeSpan? minimumPlanInterval = null,
        string controllerVersion = "0.1.0")
    {
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.routine = routine ?? new IdleRoutineController();
        this.triggers = triggers ?? [new OpeningTrigger(), new NewThreatTrigger(), new StalledEconomyTrigger(), new TechTransitionTrigger()];
        this.minimumPlanInterval = minimumPlanInterval ?? TimeSpan.FromSeconds(20);
        View = new PlayerView(house, fields);
        ControllerVersion = controllerVersion;
    }

    public string ControllerId => "bindery.playbook/" + planner.PlannerId;

    public string ControllerVersion { get; }

    public PlayerView View { get; }

    public Playbook Current { get; private set; } = Playbook.Empty;

    public Task<ControllerStep> ObserveAsync(RawObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        View.Apply(observation);
        List<string> notes = [];
        PlaybookRevision? revision = Collect(notes);

        foreach (IPlaybookTrigger trigger in triggers)
        {
            if (trigger.Detect(View, observation) is not { } name) continue;
            if (inFlight is not null)
            {
                notes.Add($"trigger {name} dropped: a plan for {inFlightTrigger} is in flight");
            }
            else if (lastPlanAt is { } last && observation.ReceivedAt - last < minimumPlanInterval && name != OpeningTrigger.Name)
            {
                notes.Add($"trigger {name} dropped: last plan was {(observation.ReceivedAt - last).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s ago");
            }
            else
            {
                notes.Add($"trigger {name}: planning with {planner.PlannerId}");
                lastPlanAt = observation.ReceivedAt;
                inFlightTrigger = name;
                PlanningRequest request = new(name, Current, View.Summarize());
                // Called directly, not on the thread pool: a planner that
                // answers synchronously applies on this very observation, and
                // one that awaits I/O stays in flight. Either way the order
                // of decisions depends only on the observations.
                try
                {
                    inFlight = planner.PlanAsync(request, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    inFlight = Task.FromException<Playbook>(exception);
                }
                revision ??= Collect(notes);
            }
        }

        IReadOnlyList<PlayerCommand> commands = routine.Decide(Current, View, observation);
        return Task.FromResult(new ControllerStep(commands, revision, notes.Count == 0 ? null : notes));
    }

    /// <summary>Waits for an in-flight plan, for tests and for the end of a match.</summary>
    public async Task SettleAsync()
    {
        if (inFlight is null) return;
        try { await inFlight.ConfigureAwait(false); }
        catch (Exception) when (inFlight.IsFaulted || inFlight.IsCanceled) { }
    }

    private PlaybookRevision? Collect(List<string> notes)
    {
        if (inFlight is not { IsCompleted: true } done) return null;
        string trigger = inFlightTrigger!;
        inFlight = null;
        inFlightTrigger = null;
        if (done.IsCompletedSuccessfully)
        {
            Current = done.Result;
            return new PlaybookRevision(trigger, $"r{Current.Revision.ToString(CultureInfo.InvariantCulture)}: {Current.Text}");
        }
        notes.Add($"planner failed on {trigger}: {done.Exception?.GetBaseException().Message ?? "cancelled"}; keeping r{Current.Revision.ToString(CultureInfo.InvariantCulture)}");
        return null;
    }
}
