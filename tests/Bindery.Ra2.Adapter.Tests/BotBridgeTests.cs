// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class BotBridgeTests
{
    private static readonly MapInfo Map = new(
        "training-1",
        64,
        64,
        [new Region(new RegionId(1), "Base", new Cell(10, 10), 8, true, true, false)],
        [],
        []);

    private static NormalizedObservation Event(string eventType, object payload, string id = "evt-1") =>
        new(
            $"derived-{id}",
            eventType,
            JsonSerializer.SerializeToElement(payload, BotJson.Options),
            [id],
            [new SourceRange("capture-1", 1, 1, "hash")],
            "1.0.0",
            "adapter-test",
            "bindery.ra2.normalizer",
            "0.1.0");

    // --- fog: an enemy entity is EXCLUDED when the telemetry lacks a visibility flag ---

    [Fact]
    public void EnemyEntityWithoutVisibilityFlag_IsExcludedFromFrame_AndReportedMissing()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.unit.created",
            new { frame = 15, owner = 1, id = 42, type = "RHINO", x = 12, y = 12, health = 100, maxHealth = 100 }));

        // Force the failure case: if the fog boundary were not enforced this
        // enemy unit (no "visible" field at all) would leak into the frame.
        ObservationFrame frame = Assert.Single(frames);
        Assert.Empty(frame.Entities);
        Assert.Contains(assembler.MissingFields.Entries, e => e.Field == "visible" && e.EventType == "game.unit.created");
    }

    [Fact]
    public void EnemyEntityVisibleTrue_IsIncludedInFrame()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);

        assembler.Ingest(Event(
            "game.unit.created",
            new { frame = 1, owner = 1, id = 42, type = "RHINO", x = 12, y = 12, health = 100, maxHealth = 100, visible = true }));
        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.economy.credits",
            new { frame = 15, owner = 0, credits = 5000 }));

        ObservationFrame frame = Assert.Single(frames);
        ObservedEntity entity = Assert.Single(frame.Entities);
        Assert.Equal(new EntityId(42), entity.Id);
        Assert.Equal(new PlayerId(1), entity.Owner);
    }

    [Fact]
    public void EnemyEntityVisibleFalse_IsExcluded_ButNotReportedMissing()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.unit.created",
            new { frame = 15, owner = 1, id = 42, type = "RHINO", x = 12, y = 12, health = 100, maxHealth = 100, visible = false }));

        ObservationFrame frame = Assert.Single(frames);
        Assert.Empty(frame.Entities);
        Assert.DoesNotContain(assembler.MissingFields.Entries, e => e.Field == "visible");
    }

    [Fact]
    public void OwnEntity_IsIncludedRegardlessOfVisibilityField()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.unit.created",
            new { frame = 15, owner = 0, id = 7, type = "GIREVOLVER", x = 10, y = 10, health = 50, maxHealth = 50 }));

        ObservationFrame frame = Assert.Single(frames);
        ObservedEntity entity = Assert.Single(frame.Entities);
        Assert.Equal(new PlayerId(0), entity.Owner);
    }

    // --- missing fields are reported and the entity is excluded, values never invented ---

    [Fact]
    public void EntityCreated_MissingHealthField_IsExcluded_AndReportedMissing()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.unit.created",
            new { frame = 15, owner = 0, id = 7, type = "GIREVOLVER", x = 10, y = 10, maxHealth = 50 }));

        ObservationFrame frame = Assert.Single(frames);
        Assert.Empty(frame.Entities);
        MissingFieldEntry entry = Assert.Single(assembler.MissingFields.Entries);
        Assert.Equal("health", entry.Field);
        Assert.Equal("game.unit.created", entry.EventType);
        Assert.Equal(1, assembler.MissingFields.CountsByField["health"]);
    }

    [Fact]
    public void EntityCreated_MissingPosition_IsExcluded_AndReportedMissing_ForBothCoordinates()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.unit.created",
            new { frame = 15, owner = 0, id = 7, type = "GIREVOLVER", health = 50, maxHealth = 50 }));

        ObservationFrame frame = Assert.Single(frames);
        Assert.Empty(frame.Entities);
        Assert.Contains(assembler.MissingFields.Entries, e => e.Field == "x");
        Assert.Contains(assembler.MissingFields.Entries, e => e.Field == "y");
    }

    [Fact]
    public void Event_MissingFrameField_IsSkippedEntirely_AndReportedMissing()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.economy.credits",
            new { owner = 0, credits = 5000 }));

        Assert.Empty(frames);
        MissingFieldEntry entry = Assert.Single(assembler.MissingFields.Entries);
        Assert.Equal("frame", entry.Field);
        Assert.Equal("event_skipped", entry.Effect);
    }

    // --- credits and power map into the frame, gated by owner ---

    [Fact]
    public void Credits_ForControlledPlayer_MapIntoFrame()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Soviet, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.economy.credits",
            new { frame = 15, owner = 0, credits = 7500 }));

        ObservationFrame frame = Assert.Single(frames);
        Assert.Equal(7500, frame.Credits);
    }

    [Fact]
    public void Credits_ForAnotherPlayer_AreIgnored_AndNotReportedMissing()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Soviet, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.economy.credits",
            new { frame = 15, owner = 1, credits = 999999 }));

        ObservationFrame frame = Assert.Single(frames);
        Assert.Equal(0, frame.Credits);
        Assert.Empty(assembler.MissingFields.Entries);
    }

    [Fact]
    public void Power_ForControlledPlayer_MapsProducedAndDrainedIntoFrame()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Soviet, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.economy.power",
            new { frame = 15, owner = 0, produced = 200, drained = 150 }));

        ObservationFrame frame = Assert.Single(frames);
        Assert.Equal(200, frame.Power.Produced);
        Assert.Equal(150, frame.Power.Drained);
        Assert.True(frame.Power.Surplus == 50);
    }

    // --- cadence ---

    [Fact]
    public void Ingest_BelowCadenceBoundary_EmitsNoFrame()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.economy.credits",
            new { frame = 5, owner = 0, credits = 1000 }));

        Assert.Empty(frames);
    }

    [Fact]
    public void Ingest_LargeFrameGap_EmitsOneFillerFrameForEachBoundaryCrossed()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.economy.credits",
            new { frame = 46, owner = 0, credits = 1000 }));

        Assert.Equal(3, frames.Count);
        Assert.Equal(15, frames[0].Time.Frame);
        Assert.Equal(30, frames[1].Time.Frame);
        Assert.Equal(45, frames[2].Time.Frame);
    }

    // --- destruction removes the entity and emits the right event kind ---

    [Fact]
    public void UnitKilled_OfOwnUnit_IsALossNotAKill()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);
        assembler.Ingest(Event(
            "game.unit.created",
            new { frame = 1, owner = 0, id = 7, type = "GIREVOLVER", x = 10, y = 10, health = 50, maxHealth = 50 }));

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event(
            "game.unit.killed",
            new { frame = 15, id = 7 }));

        ObservationFrame frame = Assert.Single(frames);
        Assert.Empty(frame.Entities);
        Assert.DoesNotContain(frame.Events, static e => e.Kind == GameEventKind.EntityKilledByUs);
        Assert.Contains(frame.Events, e => e.Kind == GameEventKind.EntityDestroyed && e.Entity == new EntityId(7) && e.Owner == new PlayerId(0));
    }

    [Fact]
    public void UnitKilled_OfVisibleEnemy_IsOurKillOnlyWhenTheKillerIsUs()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);
        assembler.Ingest(Event("game.unit.created", new { frame = 1, owner = 1, id = 8, type = "E2", x = 10, y = 10, health = 50, maxHealth = 50, visible = true }));
        assembler.Ingest(Event("game.unit.created", new { frame = 2, owner = 1, id = 9, type = "E2", x = 11, y = 10, health = 50, maxHealth = 50, visible = true }));

        List<ObservationFrame> frames = [];
        frames.AddRange(assembler.Ingest(Event("game.unit.killed", new { frame = 3, id = 8, killer = 0 })));
        frames.AddRange(assembler.Ingest(Event("game.unit.killed", new { frame = 15, id = 9 })));

        List<GameEvent> events = [.. frames.SelectMany(static f => f.Events)];
        Assert.Contains(events, static e => e.Kind == GameEventKind.EntityKilledByUs && e.Entity == new EntityId(8));
        Assert.DoesNotContain(events, static e => e.Kind == GameEventKind.EntityKilledByUs && e.Entity == new EntityId(9));
        Assert.Contains(events, static e => e.Kind == GameEventKind.EntityDestroyed && e.Entity == new EntityId(9));
    }

    [Fact]
    public void AnEnemyThatDrivesIntoFog_LeavesTheFrame_AndItsDeathInFogIsNotReported()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);
        assembler.Ingest(Event("game.unit.created", new { frame = 1, owner = 1, id = 8, type = "HTNK", x = 10, y = 10, health = 50, maxHealth = 50, visible = true }));
        assembler.Ingest(Event("game.unit.created", new { frame = 2, owner = 1, id = 8, type = "HTNK", x = 12, y = 10, health = 50, maxHealth = 50, visible = false }));

        List<ObservationFrame> frames = [];
        frames.AddRange(assembler.Ingest(Event("game.economy.credits", new { frame = 16, owner = 0, credits = 1000 })));
        frames.AddRange(assembler.Ingest(Event("game.unit.destroyed", new { frame = 20, id = 8 })));
        frames.AddRange(assembler.Ingest(Event("game.economy.credits", new { frame = 31, owner = 0, credits = 1000 })));

        Assert.NotEmpty(frames);
        Assert.All(frames, static f => Assert.DoesNotContain(f.Entities, static e => e.Id == new EntityId(8)));
        Assert.DoesNotContain(frames.SelectMany(static f => f.Events), static e => e.Entity == new EntityId(8) && e.Kind != GameEventKind.EntityCreated);
    }

    [Fact]
    public void AnEnemyDeathTheTelemetryMarksInvisible_IsNotReported()
    {
        Ra2ObservationAssembler assembler = new(new PlayerId(0), Faction.Allied, Map, frameCadence: 15);
        assembler.Ingest(Event("game.unit.created", new { frame = 1, owner = 1, id = 8, type = "HTNK", x = 10, y = 10, health = 50, maxHealth = 50, visible = true }));

        IReadOnlyList<ObservationFrame> frames = assembler.Ingest(Event("game.unit.destroyed", new { frame = 15, id = 8, visible = false }));

        Assert.DoesNotContain(frames.SelectMany(static f => f.Events), static e => e.Entity == new EntityId(8) && e.Kind != GameEventKind.EntityCreated);
    }

    // --- command envelope mapping, for every GameCommand subtype ---

    private sealed class RecordingTransport : IRa2CommandTransport
    {
        public List<Ra2CommandEnvelope> Sent { get; } = [];

        public Task SendAsync(Ra2CommandEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Sent.Add(envelope);
            return Task.CompletedTask;
        }
    }

    public static IEnumerable<object[]> AllCommandKinds()
    {
        yield return [new ProduceCommand("op", "GAWEAP", QueueKind.Building), "produce"];
        yield return [new CancelProductionCommand("op", "GAWEAP", QueueKind.Building), "cancel_production"];
        yield return [new PlaceBuildingCommand("op", "GAWEAP", new Cell(1, 2)), "place_building"];
        yield return [new SellCommand("op", new EntityId(1)), "sell"];
        yield return [new MoveCommand("op", [new EntityId(1), new EntityId(2)], new Cell(3, 4)), "move"];
        yield return [new AttackMoveCommand("op", [new EntityId(1)], new Cell(3, 4)), "attack_move"];
        yield return [new AttackCommand("op", [new EntityId(1)], new EntityId(9)), "attack"];
        yield return [new StopCommand("op", [new EntityId(1)]), "stop"];
        yield return [new DeployCommand("op", new EntityId(1)), "deploy"];
        yield return [new RepairCommand("op", new EntityId(1), new EntityId(2)), "repair"];
        yield return [new HarvestCommand("op", new EntityId(1), new Cell(5, 6)), "harvest"];
        yield return [new SetRallyPointCommand("op", new EntityId(1), new Cell(7, 8)), "set_rally_point"];
        yield return [new LaunchSuperweaponCommand("op", new EntityId(1), new Cell(9, 9)), "launch_superweapon"];
    }

    [Theory]
    [MemberData(nameof(AllCommandKinds))]
    public void ToEnvelope_MapsEveryGameCommandSubtype_ToItsDocumentedKind(GameCommand command, string expectedKind)
    {
        Ra2CommandEnvelope envelope = Ra2CommandSink.ToEnvelope(command);

        Assert.Equal(Ra2CommandEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);
        Assert.Equal(expectedKind, envelope.Kind);
        Assert.Equal("op", envelope.Controller);
        Assert.Equal(JsonValueKind.Object, envelope.Fields.ValueKind);
    }

    [Fact]
    public async Task Ra2CommandSink_BuffersUntilFlush_ThenSendsInSubmissionOrder()
    {
        RecordingTransport transport = new();
        Ra2CommandSink sink = new(transport);

        sink.Submit(new DeployCommand("op", new EntityId(1)));
        sink.Submit(new StopCommand("op", [new EntityId(2)]));
        Assert.Equal(2, sink.BufferedCount);
        Assert.Empty(transport.Sent);

        await sink.FlushAsync();

        Assert.Equal(0, sink.BufferedCount);
        Assert.Equal(2, transport.Sent.Count);
        Assert.Equal("deploy", transport.Sent[0].Kind);
        Assert.Equal("stop", transport.Sent[1].Kind);
    }

    private sealed class FailOnceTransport : IRa2CommandTransport
    {
        private bool failed;

        public List<Ra2CommandEnvelope> Sent { get; } = [];

        public Task SendAsync(Ra2CommandEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (!failed)
            {
                failed = true;
                throw new IOException("transport down");
            }
            Sent.Add(envelope);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Ra2CommandSink_AFailedSend_KeepsTheEnvelope_AndARetrySendsEverythingInOrder()
    {
        FailOnceTransport transport = new();
        Ra2CommandSink sink = new(transport);
        sink.Submit(new StopCommand("op", [new EntityId(1)]));
        sink.Submit(new StopCommand("op", [new EntityId(2)]));

        await Assert.ThrowsAsync<IOException>(() => sink.FlushAsync());
        Assert.Equal(2, sink.BufferedCount);

        await sink.FlushAsync();

        Assert.Equal(0, sink.BufferedCount);
        Assert.Equal([1u, 2u], transport.Sent.Select(static e => e.Fields.GetProperty("units")[0].GetUInt32()));
    }

    // --- MapInfoLoader ---

    [Fact]
    public void MapInfoLoader_RoundTripsMapInfoJson()
    {
        string json = JsonSerializer.Serialize(Map, BotJson.Options);

        MapInfo loaded = MapInfoLoader.Parse(json);

        Assert.Equal(Map.MapId, loaded.MapId);
        Assert.Single(loaded.Regions);
        Assert.Equal(Map.Regions[0].Id, loaded.Regions[0].Id);
    }
}
