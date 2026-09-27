// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text.Json;
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>
/// End to end over the retail seam: recorded ra2yrcpp telemetry → normalizer → assembler → a real
/// <see cref="BotRuntime"/> → command sink → a fake <see cref="IRa2CommandTransport"/>. Nothing here is
/// retail RA2; the telemetry is a hand-written recording in the <c>bindery.ra2.bot-observation/v1</c> shape.
/// </summary>
public sealed class Ra2BotHostTests
{
    private static readonly MapInfo Map = new(
        "host-test",
        64,
        64,
        [
            new Region(new RegionId(1), "base", new Cell(10, 10), 8, true, true, false),
            new Region(new RegionId(2), "enemy", new Cell(50, 50), 8, true, false, false),
        ],
        [new RegionLink(new RegionId(1), new RegionId(2), 50, true, false)],
        [new OreField(new RegionId(1), new Cell(12, 12), 10_000, false)]);

    private static RawObservation Raw(ulong sequence, string type, object payload) =>
        new(
            $"raw-{sequence}",
            "capture-host",
            sequence,
            type,
            "ra2yrcpp-bindery",
            "test",
            DateTimeOffset.UnixEpoch,
            JsonSerializer.SerializeToElement(payload, BotJson.Options),
            $"hash-{sequence}");

    private static List<RawObservation> Recording()
    {
        List<RawObservation> raws = [];
        ulong seq = 0;
        raws.Add(Raw(++seq, Ra2TelemetryEventTypes.UnitCreated, new { frame = 1, owner = 0, id = 1, type = "AMCV", x = 10, y = 10, health = 600, maxHealth = 600 }));
        raws.Add(Raw(++seq, Ra2TelemetryEventTypes.CreditsSampled, new { frame = 1, owner = 0, credits = 10_000 }));
        for (int second = 1; second <= 8; second++)
        {
            raws.Add(Raw(++seq, Ra2TelemetryEventTypes.CreditsSampled, new { frame = second * 15, owner = 0, credits = 10_000 }));
            raws.Add(Raw(++seq, Ra2TelemetryEventTypes.PowerSampled, new { frame = second * 15, owner = 0, produced = 0, drained = 0 }));
            if (second == 1)
            {
                // The MCV deploys into a construction yard (a new id), as RA2 does.
                raws.Add(Raw(++seq, Ra2TelemetryEventTypes.UnitDestroyed, new { frame = 20, id = 1 }));
                raws.Add(Raw(++seq, Ra2TelemetryEventTypes.BuildingPlaced, new { frame = 20, owner = 0, id = 2, type = "GAYARD", x = 10, y = 10, health = 1000, maxHealth = 1000 }));
            }
        }
        raws.Add(Raw(++seq, Ra2TelemetryEventTypes.MatchEnded, new { frame = 135 }));
        // Anything after the end of the match must not be read.
        raws.Add(Raw(++seq, Ra2TelemetryEventTypes.CreditsSampled, new { frame = 150, owner = 0, credits = 1 }));
        return raws;
    }

    [Fact]
    public async Task Host_drives_telemetry_through_the_bot_to_the_transport_until_the_match_ends()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        using BotRuntime runtime = StandardBot.Create(rules, PlaybookLibrary.LoadDefault(), new PlaybookSelector());
        RecordingTransport transport = new();
        RecordedSource source = new(Recording());
        Ra2BotHost host = new(runtime, new Ra2ObservationAssembler(new PlayerId(0), Faction.Allied, Map), new Ra2CommandSink(transport));

        Ra2BotHostReport report = await host.RunAsync(source);

        // The deploy decision came from the bot's tactical layer, and production from its operational layer,
        // both reaching the transport as bot-command/v1 envelopes.
        Ra2CommandEnvelope deploy = Assert.Single(transport.Sent, static e => e.Kind == "deploy");
        Assert.Equal(1u, deploy.Fields.GetProperty("unit").GetUInt32());
        Assert.Contains(transport.Sent, static e => e.Kind == "produce");
        Assert.All(transport.Sent, static e => Assert.Equal(Ra2CommandEnvelope.CurrentSchemaVersion, e.SchemaVersion));

        Assert.True(report.MatchEnded);
        Assert.Equal(9, report.Frames);
        Assert.Equal(transport.Sent.Count, report.CommandsSent);
        Assert.Equal(runtime.Metrics.Frames, report.Frames);
        Assert.Equal(source.Yielded, report.RawEvents);
        Assert.False(source.ReadPastEnd, "the host must stop reading when the match ends");
        Assert.Empty(report.MissingFields.Entries);
    }

    [Fact]
    public async Task Host_ends_on_a_match_end_between_cadence_boundaries()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        using BotRuntime runtime = StandardBot.Create(rules, PlaybookLibrary.LoadDefault(), new PlaybookSelector());
        List<RawObservation> raws =
        [
            Raw(1, Ra2TelemetryEventTypes.CreditsSampled, new { frame = 15, owner = 0, credits = 5 }),
            Raw(2, Ra2TelemetryEventTypes.MatchEnded, new { frame = 21 }),
        ];
        Ra2BotHost host = new(runtime, new Ra2ObservationAssembler(new PlayerId(0), Faction.Allied, Map), new Ra2CommandSink(new RecordingTransport()));

        Ra2BotHostReport report = await host.RunAsync(new RecordedSource(raws));

        Assert.True(report.MatchEnded);
        Assert.Equal(2, report.Frames);
    }

    [Fact]
    public async Task Cancelling_the_run_returns_the_report_so_far_with_the_match_not_ended()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        using BotRuntime runtime = StandardBot.Create(rules, PlaybookLibrary.LoadDefault(), new PlaybookSelector());
        RecordingTransport transport = new();
        RecordedSource source = new(Recording());
        Ra2BotHost host = new(runtime, new Ra2ObservationAssembler(new PlayerId(0), Faction.Allied, Map), new Ra2CommandSink(transport));
        using CancellationTokenSource cancel = new();
        host.FrameTicked += (_, ticked) => { if (ticked.Frame.Time.Frame >= 45) cancel.Cancel(); };

        Ra2BotHostReport report = await host.RunAsync(source, cancel.Token);

        Assert.False(report.MatchEnded);
        Assert.InRange(report.Frames, 3, 8);
        Assert.Equal(runtime.Metrics.Frames, report.Frames);
        Assert.Equal(transport.Sent.Count, report.CommandsSent);
        Assert.Equal(source.Yielded, report.RawEvents);
    }

    [Fact]
    public async Task Host_flushes_each_frame_before_the_next_frame_is_ticked()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        using BotRuntime runtime = StandardBot.Create(rules, PlaybookLibrary.LoadDefault(), new PlaybookSelector());
        RecordingTransport transport = new();
        Ra2BotHost host = new(runtime, new Ra2ObservationAssembler(new PlayerId(0), Faction.Allied, Map), new Ra2CommandSink(transport));
        List<(long Frame, int SentBefore)> ticks = [];
        host.FrameTicked += (_, e) => ticks.Add((e.Frame.Time.Frame, transport.Sent.Count - e.Commands.Count));

        await host.RunAsync(new RecordedSource(Recording()));

        // Each frame's commands are on the wire before the following frame is processed.
        int sentSoFar = 0;
        foreach ((long _, int sentBefore) in ticks)
        {
            Assert.True(sentBefore >= sentSoFar);
            sentSoFar = sentBefore;
        }
        Assert.Equal(9, ticks.Count);
    }

    [Fact]
    public async Task Host_reports_missing_telemetry_fields_instead_of_inventing_them()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        using BotRuntime runtime = StandardBot.Create(rules, PlaybookLibrary.LoadDefault(), new PlaybookSelector());
        List<RawObservation> raws =
        [
            Raw(1, Ra2TelemetryEventTypes.UnitCreated, new { frame = 1, owner = 1, id = 9, type = "HTNK", x = 50, y = 50, health = 600, maxHealth = 600 }),
            Raw(2, Ra2TelemetryEventTypes.CreditsSampled, new { frame = 15, owner = 0, credits = 5 }),
        ];
        Ra2BotHost host = new(runtime, new Ra2ObservationAssembler(new PlayerId(0), Faction.Allied, Map), new Ra2CommandSink(new RecordingTransport()));

        Ra2BotHostReport report = await host.RunAsync(new RecordedSource(raws));

        Assert.False(report.MatchEnded);
        Assert.Contains(report.MissingFields.Entries, static e => e.Field == "visible");
        Assert.Empty(runtime.CurrentBelief!.Enemies);
    }

    [Fact]
    public async Task Host_replays_an_ndjson_recording_to_the_same_commands_as_the_live_stream()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        string path = Path.Combine(Path.GetTempPath(), $"bindery-host-{Guid.NewGuid():N}.ndjson");
        try
        {
            RecordedRa2TelemetrySource.WriteNdjson(path, Recording());

            RecordingTransport live = new();
            using (BotRuntime runtime = StandardBot.Create(rules, PlaybookLibrary.LoadDefault(), new PlaybookSelector()))
            {
                await new Ra2BotHost(runtime, new Ra2ObservationAssembler(new PlayerId(0), Faction.Allied, Map), new Ra2CommandSink(live))
                    .RunAsync(new RecordedSource(Recording()));
            }

            RecordingTransport replayed = new();
            RecordedRa2TelemetrySource recorded = RecordedRa2TelemetrySource.FromNdjson(path);
            using (BotRuntime runtime = StandardBot.Create(rules, PlaybookLibrary.LoadDefault(), new PlaybookSelector()))
            {
                Ra2BotHostReport report = await new Ra2BotHost(runtime, new Ra2ObservationAssembler(new PlayerId(0), Faction.Allied, Map), new Ra2CommandSink(replayed))
                    .RunAsync(recorded);
                Assert.True(report.MatchEnded);
            }

            Assert.Equal(Recording().Count, recorded.Capture.RawEventCount);
            Assert.Equal(
                live.Sent.Select(static e => $"{e.Kind}|{e.Controller}|{e.Fields.GetRawText()}"),
                replayed.Sent.Select(static e => $"{e.Kind}|{e.Controller}|{e.Fields.GetRawText()}"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class RecordingTransport : IRa2CommandTransport
    {
        public List<Ra2CommandEnvelope> Sent { get; } = [];

        public Task SendAsync(Ra2CommandEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Sent.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordedSource(IReadOnlyList<RawObservation> raws) : IRa2TelemetrySource
    {
        public Ra2TelemetryCapture Capture { get; } = new("recording", new Ra2YrcppEndpoint("127.0.0.1", 1), true, raws.Count, null);

        public int Yielded { get; private set; }

        public bool ReadPastEnd { get; private set; }

        public async IAsyncEnumerable<RawObservation> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            bool ended = false;
            foreach (RawObservation raw in raws)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ended) ReadPastEnd = true;
                Yielded++;
                yield return raw;
                if (raw.EventType == Ra2TelemetryEventTypes.MatchEnded) ended = true;
                await Task.Yield();
            }
        }
    }
}
