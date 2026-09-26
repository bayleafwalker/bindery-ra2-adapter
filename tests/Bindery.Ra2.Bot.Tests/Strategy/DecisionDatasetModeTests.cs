// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Strategy;

/// <summary>
/// Decisions made on oracle frames saw the whole map; a fog-respecting (belief) distilled arm must not be fitted
/// to them, and a dataset must say which kind it holds.
/// </summary>
public sealed class DecisionDatasetModeTests
{
    private static IReadOnlyList<DecisionRecord> Log(string? mode, int count = 20)
    {
        List<DecisionRecord> records = [];
        for (int i = 0; i < count; i++)
        {
            StrategicFeatures features = Fx.Features(100 + i, credits: 1000 + 500 * i);
            StrategicIntent intent = Fx.Intent($"llm-{i}", i % 2 == 0 ? "allied-boom" : "allied-grizzly-timing",
                i % 2 == 0 ? StrategicPosture.Boom : StrategicPosture.Pressure, issuedAt: 100 + i, source: IntentSource.Llm);
            Dictionary<string, object?> data = new()
            {
                ["role"] = "Primary",
                ["renewal"] = false,
                ["faction"] = features.Faction,
                ["featureVersion"] = FeatureVector.Version,
                ["features"] = FeatureVector.Encode(features),
                ["intent"] = IntentJson.ToElement(intent),
            };
            if (mode is not null) data["mode"] = mode;
            records.Add(new DecisionRecord(DecisionRecordKinds.IntentActivated, features.Time, features.SnapshotVersion, BotJson.ToElement(data)));
        }
        return records;
    }

    [Fact]
    public void Examples_carry_the_observation_mode_of_the_log_they_came_from()
    {
        Assert.All(DecisionDataset.FromDecisionLog(Log("Oracle")).Examples, e => Assert.Equal(ObservationMode.Oracle, e.Mode));
        Assert.All(DecisionDataset.FromDecisionLog(Log("Belief")).Examples, e => Assert.Equal(ObservationMode.Belief, e.Mode));

        // A log that does not record the mode takes the one its caller knows the run was played in.
        Assert.All(DecisionDataset.FromDecisionLog(Log(null), mode: ObservationMode.Oracle).Examples, e => Assert.Equal(ObservationMode.Oracle, e.Mode));
        Assert.All(DecisionDataset.FromDecisionLog(Log(null)).Examples, e => Assert.Equal(ObservationMode.Belief, e.Mode));
    }

    [Fact]
    public void The_mode_survives_an_ndjson_round_trip()
    {
        DecisionDataset oracle = DecisionDataset.FromDecisionLog(Log("Oracle"));
        using StringWriter writer = new();
        oracle.WriteNdjson(writer);
        DecisionDataset read = DecisionDataset.ReadNdjson(new StringReader(writer.ToString()));
        Assert.Equal(oracle.Count, read.Count);
        Assert.All(read.Examples, e => Assert.Equal(ObservationMode.Oracle, e.Mode));
        Assert.True(read.HasOracleExamples);
    }

    [Fact]
    public void A_belief_distilled_strategist_does_not_train_on_oracle_decisions()
    {
        DistilledStrategist belief = new(DecisionDataset.FromDecisionLog(Log("Oracle")), new PlaybookSelector());
        Assert.Equal(0, belief.TrainedOn);
        Assert.Equal(20, belief.OracleExamplesIgnored);

        DistilledStrategist oracle = new(DecisionDataset.FromDecisionLog(Log("Oracle")), new PlaybookSelector(),
            new DistilledOptions(Mode: ObservationMode.Oracle));
        Assert.Equal(20, oracle.TrainedOn);

        DistilledStrategist fromBelief = new(DecisionDataset.FromDecisionLog(Log("Belief")), new PlaybookSelector());
        Assert.Equal(20, fromBelief.TrainedOn);
    }
}
