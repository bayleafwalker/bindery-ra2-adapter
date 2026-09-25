// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

public sealed class IntentDraftMapperTests
{
    /// <summary>The inverse of the mapper, used to prove the round trip loses nothing.</summary>
    private static IntentDraft ToDraft(StrategicIntent intent, double expiresInSeconds) =>
        new()
        {
            PlaybookId = intent.PlaybookId,
            Posture = intent.Posture.ToString(),
            Parameters = intent.PlaybookParameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new DraftParameter { Name = p.Key, Value = p.Value }).ToList(),
            Objectives = intent.Objectives.Select(o => new DraftObjective { Kind = o.Kind.ToString(), RegionId = o.Region?.Value, TypeId = o.TypeId, Priority = o.Priority }).ToList(),
            Budget = new DraftBudget { Economy = intent.Budget.Economy, Army = intent.Budget.Army, Tech = intent.Budget.Tech, Defense = intent.Budget.Defense },
            Composition = intent.Composition.Select(c => new DraftComposition { Role = c.Role.ToString(), MinShare = c.MinShare, MaxShare = c.MaxShare }).ToList(),
            RegionsOfInterest = intent.RegionsOfInterest.Select(r => r.Value).ToList(),
            AttackConditions = intent.AttackConditions.Select(ToDraft).ToList(),
            AbortTriggers = intent.AbortTriggers.Select(ToDraft).ToList(),
            ReplanTriggers = intent.ReplanTriggers.Select(ToDraft).ToList(),
            ExpiresInSeconds = expiresInSeconds,
            Confidence = intent.Confidence,
            Assumptions = intent.Assumptions.ToList(),
            Rationale = intent.Rationale ?? string.Empty,
        };

    private static DraftCondition ToDraft(Condition c) =>
        new() { Metric = c.Metric.ToString(), Op = c.Op.ToString(), Threshold = c.Threshold, RegionId = c.Region?.Value };

    [Fact]
    public void Round_trip_through_json_preserves_every_field()
    {
        StrategicFeatures features = ClaudeFixtures.Features(version: 42, seconds: 600);
        StrategicIntent original = new(
            IntentId: "claude-strategic/42",
            Source: IntentSource.Llm,
            BasedOnSnapshotVersion: 42,
            IssuedAt: features.Time,
            ExpiresAt: features.Time.Plus(90),
            Posture: StrategicPosture.Pressure,
            PlaybookId: "allied-harass",
            PlaybookParameters: new SortedDictionary<string, double>(StringComparer.Ordinal) { ["aggression"] = 0.9, ["harvesters"] = 5 },
            Objectives: [new Objective(ObjectiveKind.Harass, new RegionId(3), null, 1), new Objective(ObjectiveKind.TechTo, null, "GATECH", 3)],
            Budget: new BudgetShares(0.2, 0.6, 0.1, 0.1),
            Composition: [new CompositionTarget(UnitRole.AntiInfantry, 0.2, 0.5), new CompositionTarget(UnitRole.Scout, 0, 0.1)],
            RegionsOfInterest: [new RegionId(3), new RegionId(5)],
            AttackConditions: [new Condition(ConditionMetric.LocalForceRatio, Comparison.Ge, 1.4, new RegionId(3))],
            AbortTriggers: [new Condition(ConditionMetric.LossesValue15s, Comparison.Gt, 2000)],
            ReplanTriggers: [new Condition(ConditionMetric.ScoutingAgeSeconds, Comparison.Gt, 60, new RegionId(5))],
            Confidence: 0.65,
            Assumptions: ["no anti-air seen", "enemy base at r3"],
            Rationale: "Pressure while enemy techs.");

        string json = JsonSerializer.Serialize(ToDraft(original, 90), IntentDraftSchema.ParseOptions);
        IntentDraft parsed = IntentDraftSchema.TryParse(json, out string? error)!;
        Assert.Null(error);
        IntentMappingResult result = new IntentDraftMapper().Map(parsed, features, "claude-strategic/42");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(BotJson.ToElement(original).GetRawText(), BotJson.ToElement(result.Intent).GetRawText());
    }

    [Fact]
    public void Intent_is_stamped_from_the_features_not_the_reply()
    {
        StrategicFeatures features = ClaudeFixtures.Features(version: 17, seconds: 250);
        StrategicIntent intent = new IntentDraftMapper().Map(ClaudeFixtures.Draft(), features, "x").Intent!;

        Assert.Equal(IntentSource.Llm, intent.Source);
        Assert.Equal(17, intent.BasedOnSnapshotVersion);
        Assert.Equal(features.Time, intent.IssuedAt);
        Assert.Equal(features.Time.Plus(120), intent.ExpiresAt);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(-5, 20)]
    [InlineData(100000, 600)]
    [InlineData(75, 75)]
    public void Expiry_is_clamped(double requested, double expected)
    {
        StrategicFeatures features = ClaudeFixtures.Features();
        IntentDraft draft = ClaudeFixtures.Draft() with { ExpiresInSeconds = requested };
        StrategicIntent intent = new IntentDraftMapper().Map(draft, features, "x").Intent!;

        Assert.Equal(expected, intent.ExpiresAt.SecondsSince(intent.IssuedAt), 3);
    }

    [Fact]
    public void Unknown_posture_fails_mapping()
    {
        IntentMappingResult result = new IntentDraftMapper().Map(ClaudeFixtures.Draft(posture: "Blitz"), ClaudeFixtures.Features(), "x");
        Assert.False(result.Succeeded);
        Assert.StartsWith("map.unknown_posture", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("boom")]
    [InlineData("0")]
    [InlineData(" Boom")]
    public void Enum_names_must_match_exactly(string posture)
    {
        Assert.False(new IntentDraftMapper().Map(ClaudeFixtures.Draft(posture: posture), ClaudeFixtures.Features(), "x").Succeeded);
    }

    [Fact]
    public void Unknown_condition_metric_fails_mapping()
    {
        IntentDraft draft = ClaudeFixtures.Draft() with
        {
            AbortTriggers = [new DraftCondition { Metric = "EnemyMorale", Op = "Gt", Threshold = 1, RegionId = null }],
        };
        IntentMappingResult result = new IntentDraftMapper().Map(draft, ClaudeFixtures.Features(), "x");
        Assert.StartsWith("map.unknown_metric", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_role_and_objective_kind_fail_mapping()
    {
        IntentDraftMapper mapper = new();
        IntentDraft badRole = ClaudeFixtures.Draft() with { Composition = [new DraftComposition { Role = "Tanks", MinShare = 0, MaxShare = 1 }] };
        IntentDraft badKind = ClaudeFixtures.Draft() with { Objectives = [new DraftObjective { Kind = "Nuke", RegionId = 1, TypeId = null, Priority = 1 }] };

        Assert.StartsWith("map.unknown_role", mapper.Map(badRole, ClaudeFixtures.Features(), "x").Error, StringComparison.Ordinal);
        Assert.StartsWith("map.unknown_objective_kind", mapper.Map(badKind, ClaudeFixtures.Features(), "x").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_parameter_fails_mapping()
    {
        IntentDraft draft = ClaudeFixtures.Draft() with
        {
            Parameters = [new DraftParameter { Name = "aggression", Value = 0.1 }, new DraftParameter { Name = "aggression", Value = 0.9 }],
        };
        Assert.StartsWith("map.duplicate_parameter", new IntentDraftMapper().Map(draft, ClaudeFixtures.Features(), "x").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_lists_from_the_wire_fail_mapping_instead_of_throwing()
    {
        string json = ClaudeFixtures.DraftJson().Replace("\"assumptions\":[\"enemy is teching to heavy armor\"]", "\"assumptions\":null", StringComparison.Ordinal);
        IntentDraft draft = IntentDraftSchema.TryParse(json, out _)!;
        Assert.NotNull(draft);
        Assert.Equal("map.null_field", new IntentDraftMapper().Map(draft, ClaudeFixtures.Features(), "x").Error);
    }

    [Fact]
    public void Mapper_does_not_judge_semantics()
    {
        // Unknown playbook, budget that does not sum to 1, out-of-range confidence:
        // all the validator's business, so the mapper passes them through untouched.
        IntentDraft draft = ClaudeFixtures.Draft(playbookId: "no-such-playbook") with
        {
            Budget = new DraftBudget { Economy = 3, Army = 3, Tech = 0, Defense = 0 },
            Confidence = 1.7,
        };
        StrategicIntent intent = new IntentDraftMapper().Map(draft, ClaudeFixtures.Features(), "x").Intent!;

        Assert.Equal("no-such-playbook", intent.PlaybookId);
        Assert.Equal(6, intent.Budget.Sum);
        Assert.Equal(1.7, intent.Confidence);
    }
}
