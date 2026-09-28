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
    string Object = "object",
    string UniqueId = "unique_id");

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

/// <summary>One of the house's own factory items, as its production events last reported it.</summary>
/// <param name="Progress">Percent done, in the telemetry's steps.</param>
/// <param name="UniqueId">The item's stable ID, when the fork reports one.</param>
public sealed record OwnProductionItem(string Type, int Progress, bool OnHold, bool Completed, uint? UniqueId);

/// <summary>
/// What one house has been allowed to see so far, built only from
/// observations its <see cref="PlayerObservationFilter"/> admitted. It never
/// sees more than the filter does; it only remembers.
/// </summary>
public sealed class PlayerView
{
    private readonly List<(DateTimeOffset At, long Credits)> credits = [];
    private readonly Dictionary<string, int> ownUnits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> ownBuildingCounts = new(StringComparer.Ordinal);
    private readonly SortedSet<string> ownBuildings = new(StringComparer.Ordinal);
    private readonly SortedSet<string> defeated = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnProductionItem> ownProduction = new(StringComparer.Ordinal);

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

    /// <summary>The house's items in its factories now, by type; an item leaves when it is placed or cancelled.</summary>
    public IReadOnlyDictionary<string, OwnProductionItem> OwnProduction => ownProduction;

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
                ownBuildingCounts[type] = ownBuildingCounts.GetValueOrDefault(type) + 1;
                ownBuildings.Add(type);
                break;
            case Ra2TelemetryEventTypes.BuildingDestroyed when own && TypeOf(observation) is { } type:
                int remaining = Math.Max(0, ownBuildingCounts.GetValueOrDefault(type) - 1);
                if (remaining > 0) ownBuildingCounts[type] = remaining;
                else ownBuildingCounts.Remove(type);
                if (remaining == 0) ownBuildings.Remove(type);
                break;
            case Ra2TelemetryEventTypes.ProductionChanged or Ra2TelemetryEventTypes.ProductionCompleted when own && TypeOf(observation) is { } type:
                if (ReadBool(observation.Payload, "gone"))
                {
                    ownProduction.Remove(type);
                    break;
                }
                ownProduction[type] = new OwnProductionItem(
                    type,
                    (int)(ReadLong(observation.Payload, "progress") ?? 0),
                    ReadBool(observation.Payload, "on_hold"),
                    ReadBool(observation.Payload, "completed"),
                    ReadLong(observation.Payload, Fields.UniqueId) is { } id and > 0 and <= uint.MaxValue ? (uint)id : null);
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

    private static bool ReadBool(JsonElement payload, string field) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(field, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;
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
        // The stable ID, when the stream has one, lets the sink refuse the order if the address was reused.
        JsonElement arguments = observation.Payload.TryGetProperty(view.Fields.UniqueId, out JsonElement id) && id.TryGetUInt32(out uint uniqueId)
            ? JsonSerializer.SerializeToElement(new { objects = new[] { mcv }, unique_ids = new[] { uniqueId } })
            : JsonSerializer.SerializeToElement(new { objects = new[] { mcv } });
        return [new PlayerCommand(PlayerCommandKinds.Deploy, arguments)];
    }
}

/// <summary>
/// A routine that hears how its orders went and explains itself in the
/// trace. <see cref="PlaybookController"/> forwards the seat's outcomes to
/// it and traces its notes as <c>controller_note</c> entries.
/// </summary>
public interface IRoutineFeedback
{
    /// <summary>One of the routine's orders ran: <paramref name="error"/> is null when the game took it.</summary>
    void Completed(PlayerCommand command, Exception? error);

    /// <summary>Notes since the last call.</summary>
    IReadOnlyList<string> TakeNotes();
}

/// <summary>
/// The opening build order: once the house's Construction Yard stands,
/// produce a power plant, barracks and refinery one at a time, and place
/// each where it fits near the yard.
/// </summary>
/// <remarks>
/// The <c>opening</c> routine (the MCV deploy) runs first on every
/// observation. The yard's type picks the faction: <c>GACNST</c> builds
/// <c>GAPOWR</c>, <c>GAPILE</c>, <c>GAREFN</c>; <c>NACNST</c> builds
/// <c>NAPOWR</c>, <c>NAHAND</c>, <c>NAREFN</c>. Another yard is noted and
/// left alone.
///
/// A finished building (<c>ra2.production.completed</c> for the house) is
/// placed with every cell of <see cref="CandidateOffsets"/> around the yard,
/// in order: the seat's game says which of them the building fits on, and it
/// goes on the first. A placement the game refuses, or one whose building is
/// still waiting in the factory <c>placementTimeout</c> (3 s) after the
/// order, is asked again on the next observation (a unit may have moved). The building leaving the factory
/// counts as placed and starts the next item. After
/// <see cref="MaximumPlacementTries"/> the routine notes it and holds; so it
/// does when a produce order is refused. Time is observation time, so a
/// replay makes the same decisions. It also means the 3 s retry happens on
/// the first admitted observation at or after 3 s: with a live seat that is
/// within one poll while anything the house sees changes, and at worst within
/// the credits heartbeat (<c>Ra2yrcppTelemetryOptions.CreditsHeartbeat</c>,
/// 10 s by default) plus one poll when nothing does. No timer is added.
/// </remarks>
public sealed class BuildOrderRoutineController : IRoutineController, IRoutineFeedback
{
    public const int MaximumPlacementTries = 12;

    public const int CellLeptons = 256;

    private static readonly Dictionary<string, string[]> orders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GACNST"] = ["GAPOWR", "GAPILE", "GAREFN"],
        ["NACNST"] = ["NAPOWR", "NAHAND", "NAREFN"],
    };

    private readonly IRoutineController? opening;
    private readonly TimeSpan placementTimeout;
    private readonly List<string> notes = [];
    private string[]? order;
    private (int X, int Y, int Z) yard;
    private int step;
    private bool producing;
    private PlayerCommand? placement;
    private DateTimeOffset placedAt;
    private bool placementFailed;
    private int tries;
    private bool holding;

    public BuildOrderRoutineController(IRoutineController? opening = null, TimeSpan? placementTimeout = null)
    {
        this.opening = opening;
        this.placementTimeout = placementTimeout ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// Cells offered around the yard: every cell 2 to 8 cells out, ring by
    /// ring, nearest first within a ring, then by angle from east (280
    /// cells, under the fork's 1024-cell PlaceQuery limit).
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> CandidateOffsets { get; } = Candidates();

    public IReadOnlyList<PlayerCommand> Decide(Playbook playbook, PlayerView view, RawObservation observation)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(observation);
        IReadOnlyList<PlayerCommand> first = opening?.Decide(playbook, view, observation) ?? [];
        return Build(view, observation) is { } next ? [.. first, next] : first;
    }

    private PlayerCommand? Build(PlayerView view, RawObservation observation)
    {
        if (holding || view.StartedAt is null) return null;
        if (order is null)
        {
            if (observation.EventType != Ra2TelemetryEventTypes.BuildingPlaced || !view.IsOwn(observation) || view.TypeOf(observation) is not { } type || !type.EndsWith("CNST", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!orders.TryGetValue(type, out order) || Coordinates(observation.Payload) is not { } at)
            {
                order = null;
                Hold($"build order: no opening for a {type} yard; holding");
                return null;
            }
            yard = at;
        }
        if (placement is not null && step < order.Length && !view.OwnProduction.ContainsKey(order[step]))
        {
            // The building left the factory: placed.
            placement = null;
            tries = 0;
            step++;
            producing = false;
        }
        if (step >= order.Length) return null;
        string item = order[step];
        if (!producing)
        {
            producing = true;
            return new PlayerCommand(PlayerCommandKinds.Produce, JsonSerializer.SerializeToElement(new { type = item }));
        }
        if (!view.OwnProduction.TryGetValue(item, out OwnProductionItem? current) || !current.Completed) return null;
        if (placement is not null && !placementFailed && observation.ReceivedAt - placedAt < placementTimeout) return null;
        if (tries >= MaximumPlacementTries)
        {
            Hold($"build order: {item} not placed after {MaximumPlacementTries} tries; holding");
            return null;
        }
        tries++;
        object[] cells = [.. CandidateOffsets.Select(o => new { x = Centre(yard.X, o.X), y = Centre(yard.Y, o.Y), z = yard.Z })];
        object arguments = current.UniqueId is { } id
            ? new { type = item, cells, unique_id = id }
            : new { type = item, cells };
        placement = new PlayerCommand(PlayerCommandKinds.PlaceBuilding, JsonSerializer.SerializeToElement(arguments));
        placedAt = observation.ReceivedAt;
        placementFailed = false;
        return placement;
    }

    public void Completed(PlayerCommand command, Exception? error)
    {
        ArgumentNullException.ThrowIfNull(command);
        // An order whose outcome is unknown may still run; only the factory tells.
        if (error is null or CommandOutcomeUnknownException || holding) return;
        if (ReferenceEquals(command, placement)) placementFailed = true;
        else if (command.Kind == PlayerCommandKinds.Produce && order is not null && step < order.Length)
            Hold($"build order: producing {order[step]} failed ({error.Message}); holding");
    }

    public IReadOnlyList<string> TakeNotes()
    {
        string[] taken = [.. notes];
        notes.Clear();
        return taken;
    }

    private void Hold(string note)
    {
        holding = true;
        notes.Add(note);
    }

    /// <summary>The centre of the cell <paramref name="offset"/> cells from the one holding <paramref name="leptons"/>.</summary>
    private static int Centre(int leptons, int offset) => (leptons / CellLeptons + offset) * CellLeptons + CellLeptons / 2;

    private static (int X, int Y, int Z)? Coordinates(JsonElement payload) =>
        payload.TryGetProperty("x", out JsonElement x) && x.TryGetInt32(out int cx)
        && payload.TryGetProperty("y", out JsonElement y) && y.TryGetInt32(out int cy)
            ? (cx, cy, payload.TryGetProperty("z", out JsonElement z) && z.TryGetInt32(out int cz) ? cz : 0)
            : null;

    private static (int X, int Y)[] Candidates()
    {
        static double Turn(int x, int y) => Math.Atan2(y, x) is var a && a < 0 ? a + 2 * Math.PI : Math.Atan2(y, x);
        return [.. Enumerable.Range(-8, 17)
            .SelectMany(static x => Enumerable.Range(-8, 17).Select(y => (X: x, Y: y)))
            .Where(static c => Math.Max(Math.Abs(c.X), Math.Abs(c.Y)) >= 2)
            .OrderBy(static c => Math.Max(Math.Abs(c.X), Math.Abs(c.Y)))
            .ThenBy(static c => c.X * c.X + c.Y * c.Y)
            .ThenBy(static c => Turn(c.X, c.Y))];
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
public sealed class PlaybookController : IPlayerController, ICommandFeedback
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
        if (routine is IRoutineFeedback feedback) notes.AddRange(feedback.TakeNotes());
        return Task.FromResult(new ControllerStep(commands, revision, notes.Count == 0 ? null : notes));
    }

    /// <summary>Passes an order's outcome to a routine that listens for it.</summary>
    public void Completed(PlayerCommand command, Exception? error) => (routine as IRoutineFeedback)?.Completed(command, error);

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
