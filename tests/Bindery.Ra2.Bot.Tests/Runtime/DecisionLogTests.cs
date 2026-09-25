// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Runtime;

public sealed class DecisionLogTests
{
    private static DecisionRecord Record(string kind, long frame, object data) =>
        new(kind, new GameTime(frame), frame, BotJson.ToElement(data));

    [Fact]
    public void Ndjson_writer_streams_the_same_bytes_the_in_memory_log_serialises()
    {
        DecisionLog memory = new();
        using MemoryStream stream = new();
        using (NdjsonDecisionLogWriter writer = new(stream, memory, leaveOpen: true))
        {
            writer.Write(Record(DecisionRecordKinds.Proposal, 1, new { a = 1, b = "x" }));
            writer.Write(Record(DecisionRecordKinds.CommandDropped, 2, new { reason = "lease.missing", value = 0.1 }));
            Assert.Equal(2, writer.Records.Count);
        }
        string text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal(memory.ToNdjson(), text);
        Assert.Equal("{\"kind\":\"strategy.proposal\",\"frame\":1,\"snapshotVersion\":1,\"data\":{\"a\":1,\"b\":\"x\"}}\n", text.Split('\n')[0] + "\n");
    }

    [Fact]
    public void Ndjson_round_trip_preserves_the_hash()
    {
        DecisionLog log = new();
        log.Write(Record("a", 1, new { x = 1.5 }));
        log.Write(Record("b", 2, new { y = new[] { 1, 2, 3 } }));
        IReadOnlyList<DecisionRecord> parsed = DecisionLogCodec.ReadAll(new StringReader(log.ToNdjson()));
        Assert.Equal(log.ComputeHash(), DecisionLogCodec.Hash(parsed));
        Assert.Equal(64, log.ComputeHash().Length);
    }

    [Fact]
    public void Hash_changes_when_any_record_changes()
    {
        DecisionLog a = new(), b = new(), c = new();
        a.Write(Record("k", 1, new { v = 1 }));
        b.Write(Record("k", 1, new { v = 1 }));
        c.Write(Record("k", 1, new { v = 2 }));
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.NotEqual(a.ComputeHash(), c.ComputeHash());
        b.Write(Record("k", 2, new { v = 1 }));
        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void Intent_json_is_canonical_and_round_trips()
    {
        StrategicIntent intent = Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            parameters: new Dictionary<string, double> { ["z"] = 1, ["a"] = 0.1 },
            objectives: [new Objective(ObjectiveKind.AttackRegion, Fx.R1, null, 2), new Objective(ObjectiveKind.TechTo, null, "gatech", 1)],
            composition: [new CompositionTarget(UnitRole.AntiAir, 0.1, 0.3)],
            regions: [Fx.R2],
            abort: [new Condition(ConditionMetric.LocalForceRatio, Comparison.Lt, 0.5, Fx.R1)]);
        JsonElement json = IntentJson.ToElement(intent);
        Assert.Equal(["a", "z"], json.GetProperty("playbookParameters").EnumerateObject().Select(p => p.Name).ToArray());

        StrategicIntent back = IntentJson.FromElement(json);
        Assert.Equal(json.GetRawText(), IntentJson.ToElement(back).GetRawText());
        Assert.Equal(intent.ExpiresAt, back.ExpiresAt);
        Assert.Equal(Fx.R1, back.AbortTriggers[0].Region);
        Assert.Equal(0.1, back.PlaybookParameters["a"]);
    }

    [Fact]
    public async Task Replay_strategist_counts_requests_it_has_no_recording_for()
    {
        ReplayStrategist replay = new([]);
        StrategistContext context = new(Fx.Features(1), Fx.Rules, Fx.Playbooks, null, [], null);
        Task<StrategistProposal?> task = replay.ProposeAsync(context);
        Assert.True(task.IsCompleted);
        Assert.Null(await task);
        Assert.Equal(1, replay.Misses);
        Assert.Equal("replay", replay.Id);
    }
}
