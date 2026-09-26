// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arbitration;

public sealed class IntentValidatorTests
{
    private readonly IntentValidator validator = new();

    private ValidationResult Validate(StrategicIntent intent, StrategicFeatures? features = null) =>
        validator.Validate(intent, Fx.Context(features ?? Fx.Features(1)));

    private static bool Has(ValidationResult result, string code, ValidationSeverity severity) =>
        result.Issues.Any(i => i.Code == code && i.Severity == severity);

    [Fact]
    public void Well_formed_intent_is_accepted_and_filled_with_playbook_defaults()
    {
        ValidationResult result = Validate(Fx.Intent("i1", "allied-boom", StrategicPosture.Boom));

        Assert.True(result.Accepted);
        StrategicIntent intent = Assert.IsType<StrategicIntent>(result.Intent);
        Assert.Equal(0.5, intent.PlaybookParameters["aggression"]);
        Assert.Equal(2, intent.PlaybookParameters["expandAt"]);
        Assert.Equal(2, intent.Composition.Count);
        Assert.Single(intent.AttackConditions);
        Assert.All(result.Issues, i => Assert.Equal(ValidationSeverity.Warning, i.Severity));
        Assert.Contains(result.Issues, i => i.Code == ValidationCodes.DefaultFilled);
    }

    [Fact]
    public void Unknown_playbook_is_rejected()
    {
        ValidationResult result = Validate(Fx.Intent("i", "no-such-playbook", StrategicPosture.Boom));
        Assert.False(result.Accepted);
        Assert.Null(result.Intent);
        Assert.True(Has(result, ValidationCodes.PlaybookUnknown, ValidationSeverity.Reject));
    }

    [Fact]
    public void Playbook_of_another_faction_is_rejected()
    {
        ValidationResult result = Validate(Fx.Intent("i", "soviet-rush", StrategicPosture.AllIn));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.PlaybookFaction, ValidationSeverity.Reject));
    }

    [Fact]
    public void Negative_budget_share_is_rejected()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, budget: new BudgetShares(0.8, 0.4, -0.2, 0)));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.BudgetNegative, ValidationSeverity.Reject));
    }

    [Fact]
    public void Budget_close_to_one_is_normalised_with_a_warning()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, budget: new BudgetShares(0.5, 0.4, 0.1, 0.1)));
        Assert.True(result.Accepted);
        Assert.True(Has(result, ValidationCodes.BudgetSum, ValidationSeverity.Warning));
        Assert.Equal(1.0, result.Intent!.Budget.Sum, 9);
        Assert.Equal(0.5 / 1.1, result.Intent.Budget.Economy, 9);
    }

    [Fact]
    public void Budget_far_from_one_is_rejected()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, budget: new BudgetShares(0.8, 0.5, 0.1, 0.1)));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.BudgetSum, ValidationSeverity.Reject));
    }

    [Fact]
    public void Budget_within_tolerance_is_left_untouched_and_zero_budget_takes_playbook_default()
    {
        ValidationResult close = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, budget: new BudgetShares(0.405, 0.4, 0.1, 0.1)));
        Assert.True(close.Accepted);
        Assert.Equal(0.405, close.Intent!.Budget.Economy);
        Assert.DoesNotContain(close.Issues, i => i.Code == ValidationCodes.BudgetSum);

        ValidationResult empty = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, budget: new BudgetShares(0, 0, 0, 0)));
        Assert.True(empty.Accepted);
        Assert.Equal(0.6, empty.Intent!.Budget.Economy);
    }

    [Theory]
    [InlineData(0.5, 0.4)]
    [InlineData(-0.1, 0.4)]
    [InlineData(0.2, 1.2)]
    public void Composition_outside_a_unit_range_is_rejected(double min, double max)
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            composition: [new CompositionTarget(UnitRole.AntiArmor, min, max)]));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.CompositionRange, ValidationSeverity.Reject));
    }

    [Fact]
    public void Composition_whose_minimums_exceed_one_is_rejected()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            composition: [new CompositionTarget(UnitRole.AntiArmor, 0.7, 0.9), new CompositionTarget(UnitRole.AntiAir, 0.5, 0.6)]));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.CompositionRange, ValidationSeverity.Reject));
    }

    [Fact]
    public void Unknown_parameters_are_dropped_and_out_of_range_parameters_clamped()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            parameters: new Dictionary<string, double> { ["aggression"] = 7, ["expandAt"] = 0, ["bogus"] = 1 }));

        Assert.True(result.Accepted);
        Assert.True(Has(result, ValidationCodes.ParamUnknown, ValidationSeverity.Warning));
        Assert.Equal(2, result.Issues.Count(i => i.Code == ValidationCodes.ParamClamped));
        Assert.False(result.Intent!.PlaybookParameters.ContainsKey("bogus"));
        Assert.Equal(1, result.Intent.PlaybookParameters["aggression"]);
        Assert.Equal(1, result.Intent.PlaybookParameters["expandAt"]);
        Assert.Equal(["aggression", "expandAt"], result.Intent.PlaybookParameters.Keys.ToArray());
    }

    [Fact]
    public void Objective_region_not_on_the_map_is_rejected_but_an_unseen_map_region_is_fine()
    {
        ValidationResult bad = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            objectives: [new Objective(ObjectiveKind.AttackRegion, new RegionId(99), null, 1)]));
        Assert.False(bad.Accepted);
        Assert.True(Has(bad, ValidationCodes.RegionUnknown, ValidationSeverity.Reject));

        // R3 has never been scouted (no RegionAgeSeconds entry) but the map is public knowledge.
        ValidationResult good = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            objectives: [new Objective(ObjectiveKind.AttackRegion, Fx.R3, null, 1)], regions: [Fx.R3]));
        Assert.True(good.Accepted);
    }

    [Fact]
    public void Region_of_interest_off_the_map_is_a_fog_rejection()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, regions: [new RegionId(42)]));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.FogRegion, ValidationSeverity.Reject));
    }

    [Fact]
    public void Attack_objective_without_region_is_rejected()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            objectives: [new Objective(ObjectiveKind.AttackRegion, null, null, 1)]));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.ObjectiveRegionRequired, ValidationSeverity.Reject));
    }

    [Fact]
    public void Unreachable_tech_goal_is_rejected_and_reachable_one_accepted()
    {
        ValidationResult bad = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            objectives: [new Objective(ObjectiveKind.TechTo, null, "gachrono", 1)]));
        Assert.False(bad.Accepted);
        Assert.True(Has(bad, ValidationCodes.TechUnreachable, ValidationSeverity.Reject));

        ValidationResult good = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            objectives: [new Objective(ObjectiveKind.TechTo, null, "gatech", 1)]));
        Assert.True(good.Accepted);
    }

    [Fact]
    public void Unseen_enemy_type_is_a_fog_rejection_until_it_has_been_observed()
    {
        StrategicIntent intent = Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            objectives: [new Objective(ObjectiveKind.DefendRegion, Fx.R0, "apoc", 1)]);

        ValidationResult unseen = Validate(intent, Fx.Features(1));
        Assert.False(unseen.Accepted);
        Assert.True(Has(unseen, ValidationCodes.FogUnknownType, ValidationSeverity.Reject));

        ValidationResult seen = Validate(intent, Fx.Features(1, knownTech: new HashSet<string> { "apoc" }));
        Assert.True(seen.Accepted);
    }

    [Fact]
    public void Proposal_older_than_the_age_limit_is_stale()
    {
        StrategicIntent intent = Fx.Intent("i", "allied-boom", StrategicPosture.Boom, issuedAt: 0);
        ValidationResult fresh = Validate(intent, Fx.Features(15));
        Assert.True(fresh.Accepted);

        ValidationResult stale = Validate(intent, Fx.Features(15.2));
        Assert.False(stale.Accepted);
        Assert.True(Has(stale, ValidationCodes.StaleSnapshot, ValidationSeverity.Reject));
    }

    [Fact]
    public void Severe_event_after_issue_makes_the_proposal_stale_but_milder_or_earlier_events_do_not()
    {
        StrategicIntent intent = Fx.Intent("i", "allied-boom", StrategicPosture.Boom, issuedAt: 2);
        StrategicEvent severeAfter = new(StrategicEventKind.BaseUnderAttack, Fx.T(3), 0.8, "base");
        StrategicEvent mildAfter = new(StrategicEventKind.LowPower, Fx.T(3), 0.69, "power");
        StrategicEvent severeBefore = new(StrategicEventKind.McvLost, Fx.T(1), 0.9, "mcv");

        ValidationResult stale = Validate(intent, Fx.Features(4, events: [severeAfter]));
        Assert.False(stale.Accepted);
        Assert.True(Has(stale, ValidationCodes.StaleEvent, ValidationSeverity.Reject));

        Assert.True(Validate(intent, Fx.Features(4, events: [mildAfter, severeBefore])).Accepted);
    }

    [Fact]
    public void Proposal_from_the_future_is_rejected()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, issuedAt: 10), Fx.Features(5));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.SnapshotFuture, ValidationSeverity.Reject));
    }

    [Fact]
    public void Expired_intent_is_rejected_and_overlong_expiry_is_capped()
    {
        ValidationResult past = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, issuedAt: 0, lifetime: 3), Fx.Features(5));
        Assert.False(past.Accepted);
        Assert.True(Has(past, ValidationCodes.ExpiryPast, ValidationSeverity.Reject));

        ValidationResult capped = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, issuedAt: 0, lifetime: 900), Fx.Features(5));
        Assert.True(capped.Accepted);
        Assert.True(Has(capped, ValidationCodes.ExpiryTooLong, ValidationSeverity.Warning));
        Assert.Equal(Fx.T(180), capped.Intent!.ExpiresAt);
    }

    [Fact]
    public void Confidence_out_of_range_is_clamped_and_nan_rejected()
    {
        ValidationResult clamped = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, confidence: 1.4));
        Assert.True(clamped.Accepted);
        Assert.Equal(1, clamped.Intent!.Confidence);
        Assert.True(Has(clamped, ValidationCodes.ConfidenceRange, ValidationSeverity.Warning));

        ValidationResult nan = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom, confidence: double.NaN));
        Assert.False(nan.Accepted);
        Assert.True(Has(nan, ValidationCodes.ConfidenceRange, ValidationSeverity.Reject));
    }

    [Fact]
    public void Region_scoped_trigger_without_region_is_rejected()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            abort: [new Condition(ConditionMetric.LocalForceRatio, Comparison.Lt, 0.5)]));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.ConditionRegionRequired, ValidationSeverity.Reject));
    }

    [Fact]
    public void All_issues_are_reported_not_just_the_first()
    {
        ValidationResult result = Validate(Fx.Intent("", "allied-boom", StrategicPosture.Boom,
            budget: new BudgetShares(2, 0, 0, 0), regions: [new RegionId(77)]));
        Assert.False(result.Accepted);
        Assert.Contains(result.Issues, i => i.Code == ValidationCodes.IntentId);
        Assert.Contains(result.Issues, i => i.Code == ValidationCodes.BudgetSum);
        Assert.Contains(result.Issues, i => i.Code == ValidationCodes.FogRegion);
    }
    [Theory]
    [InlineData("attack")]
    [InlineData("abort")]
    [InlineData("replan")]
    public void Condition_naming_an_off_map_region_is_rejected(string list)
    {
        Condition offMap = new(ConditionMetric.ScoutingAgeSeconds, Comparison.Gt, 30, new RegionId(999));
        ValidationResult result = Validate(With(list, offMap));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.RegionUnknown, ValidationSeverity.Reject));
    }

    [Theory]
    [InlineData("attack")]
    [InlineData("abort")]
    [InlineData("replan")]
    public void Condition_with_a_nan_threshold_is_rejected(string list)
    {
        ValidationResult result = Validate(With(list, new Condition(ConditionMetric.Credits, Comparison.Lt, double.NaN)));
        Assert.False(result.Accepted);
        Assert.True(Has(result, ValidationCodes.ConditionThreshold, ValidationSeverity.Reject));
    }

    [Fact]
    public void Empty_abort_triggers_are_filled_from_the_playbook()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-pressure", StrategicPosture.Pressure));
        Assert.True(result.Accepted);
        Assert.True(Fx.Playbooks.TryGet("allied-pressure", out Playbook playbook));
        Assert.Equal(playbook.AbortTriggers, result.Intent!.AbortTriggers);
        Assert.Contains(result.Issues, i => i.Code == ValidationCodes.DefaultFilled && i.Message.Contains("abort", StringComparison.Ordinal));
    }

    [Fact]
    public void Nan_parameter_becomes_the_playbook_default_with_a_warning()
    {
        ValidationResult result = Validate(Fx.Intent("i", "allied-boom", StrategicPosture.Boom,
            parameters: new Dictionary<string, double> { ["aggression"] = double.NaN }));
        Assert.True(result.Accepted);
        Assert.Equal(0.5, result.Intent!.PlaybookParameters["aggression"]);
        Assert.True(Has(result, ValidationCodes.ParamClamped, ValidationSeverity.Warning));
    }

    private static StrategicIntent With(string list, Condition condition)
    {
        StrategicIntent intent = Fx.Intent("i", "allied-boom", StrategicPosture.Boom);
        return list switch
        {
            "attack" => intent with { AttackConditions = [condition] },
            "abort" => intent with { AbortTriggers = [condition] },
            _ => intent with { ReplanTriggers = [condition] },
        };
    }
}
