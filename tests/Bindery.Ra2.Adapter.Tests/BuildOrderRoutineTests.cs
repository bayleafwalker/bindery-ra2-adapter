// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Bindery.Ra2.Adapter.Channel;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class BuildOrderRoutineTests
{
    private static readonly DateTimeOffset start = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static ulong sequence;

    // The Construction Yard's centre, in leptons, on cell (10, 20).
    private const int YardX = 10 * 256 + 128, YardY = 20 * 256 + 128;

    [Fact]
    public void CandidateCellsAreEveryCellOfRingsTwoToEightNearestFirst()
    {
        IReadOnlyList<(int X, int Y)> offsets = BuildOrderRoutineController.CandidateOffsets;

        // Every cell 2 to 8 cells out (8r per ring): the game, not a guess,
        // says which of them a building fits on.
        Assert.Equal(Enumerable.Range(2, 7).Sum(static r => 8 * r), offsets.Count);
        Assert.Equal(offsets.Count, offsets.Distinct().Count());
        Assert.Equal([(2, 0), (0, 2), (-2, 0), (0, -2)], offsets.Take(4));
        int[] rings = [.. offsets.Select(static o => Math.Max(Math.Abs(o.X), Math.Abs(o.Y)))];
        Assert.Equal(rings.Order(), rings);
        Assert.Equal(2, rings.Min());
        Assert.Equal(8, rings.Max());
        Assert.True(offsets.Count <= 1024, "the fork truncates a PlaceQuery at 1024 cells");
    }

    [Fact]
    public void AfterTheMcvDeploysItProducesPowerBarracksAndRefineryOneAtATime()
    {
        BuildOrderRoutineController routine = new(new DeployMcvRoutineController());
        Run run = new(routine);

        Assert.Empty(run.See(0, Ra2TelemetryEventTypes.MatchStarted, "{}"));
        Assert.Equal(PlayerCommandKinds.Deploy, Assert.Single(run.See(0, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"AMCV\",\"object\":161}")).Kind);
        // The enemy's yard, if the filter ever let one through, starts nothing.
        Assert.Empty(run.See(1, Ra2TelemetryEventTypes.BuildingPlaced, Yard("Soviets", "NACNST")));

        Assert.Equal(("produce", "GAPOWR"), Order(Assert.Single(run.See(5, Ra2TelemetryEventTypes.BuildingPlaced, Yard("Americans", "GACNST")))));
        Assert.Empty(run.See(6, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":9200}"));
        Assert.Empty(run.See(7, Ra2TelemetryEventTypes.ProductionChanged, Item("GAPOWR", 50, completed: false)));

        foreach ((string built, string next) in new[] { ("GAPOWR", "GAPILE"), ("GAPILE", "GAREFN") })
        {
            PlayerCommand place = Assert.Single(run.See(10, Ra2TelemetryEventTypes.ProductionCompleted, Item(built, 100, completed: true)));
            Assert.Equal(("place_building", built), Order(place));
            Assert.Empty(run.See(10, Ra2TelemetryEventTypes.ProductionChanged, Item(built, 100, completed: true)));
            Assert.Equal(("produce", next), Order(Assert.Single(run.See(11, Ra2TelemetryEventTypes.ProductionChanged, Item(built, 100, completed: true, gone: true)))));
        }
        Assert.Equal(("place_building", "GAREFN"), Order(Assert.Single(run.See(20, Ra2TelemetryEventTypes.ProductionCompleted, Item("GAREFN", 100, completed: true)))));
        Assert.Empty(run.See(21, Ra2TelemetryEventTypes.ProductionChanged, Item("GAREFN", 100, completed: true, gone: true)));
        Assert.Empty(run.See(40, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":5000}"));
    }

    [Fact]
    public void ASovietYardBuildsTheSovietOrder()
    {
        Run run = new(new BuildOrderRoutineController());
        run.See(0, Ra2TelemetryEventTypes.MatchStarted, "{}");

        Assert.Equal(("produce", "NAPOWR"), Order(Assert.Single(run.See(1, Ra2TelemetryEventTypes.BuildingPlaced, Yard("Americans", "NACNST")))));
        run.See(9, Ra2TelemetryEventTypes.ProductionCompleted, Item("NAPOWR", 100, completed: true));
        Assert.Equal(("produce", "NAHAND"), Order(Assert.Single(run.See(10, Ra2TelemetryEventTypes.ProductionChanged, Item("NAPOWR", 100, completed: true, gone: true)))));
    }

    [Fact]
    public void AFinishedBuildingGoesOnTheFirstCandidateCellWithItsStableId()
    {
        Run run = Started();

        PlayerCommand place = Assert.Single(run.See(10, Ra2TelemetryEventTypes.ProductionCompleted, Item("GAPOWR", 100, completed: true)));

        Assert.Equal(PlayerCommandKinds.PlaceBuilding, place.Kind);
        Assert.Equal("GAPOWR", place.Arguments.GetProperty("type").GetString());
        JsonElement[] cells = [.. place.Arguments.GetProperty("cells").EnumerateArray()];
        Assert.Equal(BuildOrderRoutineController.CandidateOffsets.Count, cells.Length);
        Assert.Equal(12 * 256 + 128, cells[0].GetProperty("x").GetInt32());
        Assert.Equal(YardY, cells[0].GetProperty("y").GetInt32());
        Assert.Equal(0, cells[0].GetProperty("z").GetInt32());
        Assert.False(place.Arguments.TryGetProperty("x", out _));
        Assert.Equal(701u, place.Arguments.GetProperty("unique_id").GetUInt32());
        Assert.False(place.Arguments.TryGetProperty("object", out _));
    }

    [Fact]
    public void AFailedPlacementTriesTheNextCandidateOnTheNextObservation()
    {
        Run run = Started();
        PlayerCommand first = Assert.Single(run.See(10, Ra2TelemetryEventTypes.ProductionCompleted, Item("GAPOWR", 100, completed: true)));

        run.Routine.Completed(first, new InvalidOperationException("CanPlaceHere check failed"));
        PlayerCommand second = Assert.Single(run.See(10, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":9000}"));

        // Asked again: the cells free now may differ (a unit moved off one).
        Assert.Equal(PlayerCommandKinds.PlaceBuilding, second.Kind);
        Assert.NotSame(first, second);
        Assert.Equal(first.Arguments.GetProperty("cells").GetArrayLength(), second.Arguments.GetProperty("cells").GetArrayLength());
    }

    [Fact]
    public void ABuildingStillWaitingThreeSecondsAfterItsOrderTriesTheNextCandidate()
    {
        Run run = Started();
        PlayerCommand first = Assert.Single(run.See(10, Ra2TelemetryEventTypes.ProductionCompleted, Item("GAPOWR", 100, completed: true)));
        // Accepted, or lost on the way: either way only the building leaving the factory counts.
        run.Routine.Completed(first, null);

        Assert.Empty(run.See(12, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":9000}"));
        PlayerCommand second = Assert.Single(run.See(13, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":9000}"));

        Assert.Equal(PlayerCommandKinds.PlaceBuilding, second.Kind);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void AnOrderWhoseOutcomeIsUnknownIsNotRetriedAtOnce()
    {
        Run run = Started();
        PlayerCommand first = Assert.Single(run.See(10, Ra2TelemetryEventTypes.ProductionCompleted, Item("GAPOWR", 100, completed: true)));

        run.Routine.Completed(first, new CommandOutcomeUnknownException("PlaceBuilding may still execute"));

        Assert.Empty(run.See(11, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":9000}"));
    }

    [Fact]
    public void AfterTwelveFailedTriesTheRoutineNotesItAndHolds()
    {
        Run run = Started();
        BuildOrderRoutineController routine = run.Routine;
        List<PlayerCommand> placements = [.. run.See(10, Ra2TelemetryEventTypes.ProductionCompleted, Item("GAPOWR", 100, completed: true))];
        Assert.Empty(routine.TakeNotes());
        for (int i = 0; i < 20; i++)
        {
            routine.Completed(placements[^1], new InvalidOperationException("CanPlaceHere check failed"));
            IReadOnlyList<PlayerCommand> next = run.See(11 + i, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":9000}");
            if (next.Count == 0) break;
            placements.AddRange(next);
        }

        Assert.Equal(BuildOrderRoutineController.MaximumPlacementTries, placements.Count);
        Assert.All(placements, static p => Assert.Equal(PlayerCommandKinds.PlaceBuilding, p.Kind));
        Assert.All(placements, static p => Assert.Equal(BuildOrderRoutineController.CandidateOffsets.Count, p.Arguments.GetProperty("cells").GetArrayLength()));
        string note = Assert.Single(routine.TakeNotes());
        Assert.Contains("GAPOWR", note, StringComparison.Ordinal);
        Assert.Contains("12", note, StringComparison.Ordinal);
        Assert.Empty(run.See(60, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":9000}"));
        Assert.Empty(routine.TakeNotes());
    }

    [Fact]
    public async Task TheSeatTellsThePlaybookRoutineWhichOrdersFailedAndTracesItsNotes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bindery-seat-" + Guid.NewGuid().ToString("N"));
        try
        {
            PlaybookController controller = new("Americans", new RulePlaybookPlanner(), new BuildOrderRoutineController(new DeployMcvRoutineController()));
            ChannelTests.MemoryCommands commands = new("Americans") { RejectKind = PlayerCommandKinds.PlaceBuilding };
            AgentSeat seat = new(controller, commands, new PlayerObservationFilter("Americans"));
            List<RawObservation> observations =
            [
                At(0, Ra2TelemetryEventTypes.MatchStarted, "{}"),
                At(0, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"AMCV\",\"object\":161}"),
                At(5, Ra2TelemetryEventTypes.BuildingPlaced, Yard("Americans", "GACNST")),
                At(10, Ra2TelemetryEventTypes.ProductionCompleted, Item("GAPOWR", 100, completed: true)),
            ];
            for (int i = 0; i < 12; i++) observations.Add(At(11 + i, Ra2TelemetryEventTypes.CreditsSampled, "{\"house\":\"Americans\",\"credits\":9000}"));
            observations.Add(At(40, Ra2TelemetryEventTypes.MatchEnded, "{}"));

            AgentSeatSummary summary = await seat.RunAsync(new ChannelTests.FakeTelemetry(observations), directory);

            // Deploy and produce went through; every placement was refused, one per observation, up to the cap.
            Assert.Equal(2, summary.CommandsSent);
            Assert.Equal(BuildOrderRoutineController.MaximumPlacementTries, summary.CommandsFailed);
            string[] notes = [.. (await File.ReadAllLinesAsync(summary.TracePath))
                .Select(static line => JsonDocument.Parse(line).RootElement)
                .Where(static entry => entry.GetProperty("kind").GetString() == "controller_note")
                .Select(static entry => entry.GetProperty("detail").GetString()!)];
            Assert.Contains(notes, static n => n.Contains("GAPOWR", StringComparison.Ordinal) && n.Contains("holding", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A routine past the opening: the yard is up and the power plant is in production.</summary>
    private static Run Started()
    {
        Run run = new(new BuildOrderRoutineController());
        run.See(0, Ra2TelemetryEventTypes.MatchStarted, "{}");
        Assert.Equal(("produce", "GAPOWR"), Order(Assert.Single(run.See(5, Ra2TelemetryEventTypes.BuildingPlaced, Yard("Americans", "GACNST")))));
        return run;
    }

    private static (string Kind, string? Type) Order(PlayerCommand command) =>
        (command.Kind, command.Arguments.TryGetProperty("type", out JsonElement type) ? type.GetString() : null);

    private static string Yard(string house, string type) =>
        $"{{\"house\":\"{house}\",\"type\":\"{type}\",\"object\":4097,\"kind\":\"building\",\"unique_id\":600,\"x\":{YardX},\"y\":{YardY},\"z\":0,\"visible_to\":[\"{house}\"]}}";

    private static string Item(string type, int progress, bool completed, bool gone = false) =>
        $"{{\"house\":\"Americans\",\"type\":\"{type}\",\"progress\":{progress},\"on_hold\":false,\"completed\":{(completed ? "true" : "false")},\"unique_id\":701{(gone ? ",\"gone\":true" : "")},\"visible_to\":[\"Americans\"]}}";

    private static RawObservation At(int seconds, string type, string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        ulong next = Interlocked.Increment(ref sequence);
        return new RawObservation($"bo-{next}", "capture-1", next, type, Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, start.AddSeconds(seconds), document.RootElement.Clone(), "sha256:raw");
    }

    /// <summary>The routine as the playbook controller drives it: the view first, then the decision.</summary>
    private sealed class Run(BuildOrderRoutineController routine)
    {
        private readonly PlayerView view = new("Americans");

        public BuildOrderRoutineController Routine { get; } = routine;

        public IReadOnlyList<PlayerCommand> See(int seconds, string type, string payload)
        {
            RawObservation observation = At(seconds, type, payload);
            view.Apply(observation);
            return Routine.Decide(Playbook.Empty, view, observation);
        }
    }
}
