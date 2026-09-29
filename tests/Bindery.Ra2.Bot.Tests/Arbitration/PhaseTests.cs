// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Tests.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Tests.Integration;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arbitration;

/// <summary>Playbook phases: forward-only tick-rate progression, overrides, reset, JSON, replay.</summary>
public sealed class PhaseTests
{
    private static readonly BudgetShares StartBudget = new(0.7, 0.1, 0.1, 0.1);
    private static readonly BudgetShares TechBudget = new(0.2, 0.2, 0.5, 0.1);
    private static readonly BudgetShares AttackBudget = new(0.1, 0.8, 0.05, 0.05);

    /// <summary>The test-only "expand, then tech, then attack" example. Not part of the default 12.</summary>
    internal static Playbook ExpandTechAttack() => new(
        "test-expand-tech-attack", "Expand first, tech at 60 s, attack from 120 s.", [Faction.Allied], StrategicPosture.Expand,
        StartBudget, [new CompositionTarget(UnitRole.AntiArmor, 0.4, 0.8)], ["GAREFN", "GAWEAP"], [], [], [], 45,
        [
            new PlaybookPhase("expand", []),
            new PlaybookPhase("tech", [new Condition(ConditionMetric.GameSeconds, Comparison.Ge, 60)],
                Posture: StrategicPosture.Tech, Budget: TechBudget),
            new PlaybookPhase("attack", [new Condition(ConditionMetric.GameSeconds, Comparison.Ge, 120)],
                Posture: StrategicPosture.Pressure, Budget: AttackBudget,
                Composition: [new CompositionTarget(UnitRole.AntiArmor, 0.7, 1.0)],
                AttackConditions: [new Condition(ConditionMetric.ArmyValueRatio, Comparison.Gt, 0.1)]),
        ]);

    private static PlaybookLibrary Library() => new([ExpandTechAttack(), .. Fx.Playbooks.All]);

    private readonly DecisionLog log = new();
    private readonly IntentArbiter arbiter;

    public PhaseTests()
    {
        arbiter = new IntentArbiter(Library(), null, log);
    }

    private StrategicIntent Activate(string id, string playbook, StrategicPosture posture, double at)
    {
        StrategicIntent intent = Fx.Intent(id, playbook, posture, issuedAt: at, lifetime: 1000, budget: StartBudget, confidence: 0.5 + at / 400);
        Assert.Equal(ArbitrationOutcome.Activated, arbiter.Offer(Fx.Accepted(intent), Fx.Features(at)).Outcome);
        return intent;
    }

    private IReadOnlyList<(string From, string To)> Changes() => log.OfKind(DecisionRecordKinds.PhaseChanged)
        .Select(static r => (r.Data.GetProperty("fromPhase").GetString()!, r.Data.GetProperty("toPhase").GetString()!)).ToList();

    [Fact]
    public void Phases_advance_in_order_and_never_go_back()
    {
        Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        arbiter.Update(Fx.Features(10));
        Assert.Equal(0, arbiter.PhaseIndex);
        Assert.Same(arbiter.Active, arbiter.EffectiveIntent);

        arbiter.Update(Fx.Features(60));
        Assert.Equal("tech", arbiter.PhaseName);
        arbiter.Update(Fx.Features(120));
        Assert.Equal("attack", arbiter.PhaseName);
        Assert.Equal([("expand", "tech"), ("tech", "attack")], Changes());

        // A condition that stops holding does not send the plan back.
        arbiter.Update(Fx.Features(121, ownArmy: 0));
        Assert.Equal("attack", arbiter.PhaseName);
        Assert.Equal(2, Changes().Count);
    }

    [Fact]
    public void A_later_phase_whose_conditions_already_hold_is_entered_directly()
    {
        Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        arbiter.Update(Fx.Features(130));
        Assert.Equal("attack", arbiter.PhaseName);
        Assert.Equal([("expand", "attack")], Changes());
    }

    [Fact]
    public void The_effective_intent_takes_the_phases_non_null_fields_and_keeps_the_rest()
    {
        StrategicIntent intent = Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        arbiter.Update(Fx.Features(60));
        StrategicIntent tech = arbiter.EffectiveIntent!;
        Assert.Equal(StrategicPosture.Tech, tech.Posture);
        Assert.Equal(TechBudget, tech.Budget);
        Assert.Equal(intent.Composition, tech.Composition);      // the tech phase leaves composition alone
        Assert.Equal(intent.AttackConditions, tech.AttackConditions);
        Assert.Equal(intent.IntentId, tech.IntentId);
        Assert.Same(tech, arbiter.EffectiveIntent);              // stable between phase changes

        arbiter.Update(Fx.Features(120));
        StrategicIntent attack = arbiter.EffectiveIntent!;
        Assert.Equal(AttackBudget, attack.Budget);
        Assert.Equal(0.7, attack.Composition[0].MinShare);
        Assert.Single(attack.AttackConditions);
        Assert.Equal(StartBudget, arbiter.Active!.Budget);       // the active intent itself is untouched
    }

    [Fact]
    public void A_new_intent_restarts_at_phase_zero()
    {
        Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        arbiter.Update(Fx.Features(125));
        Assert.Equal("attack", arbiter.PhaseName);

        Activate("b", "allied-turtle", StrategicPosture.Turtle, 130);
        Assert.Null(arbiter.PhaseName);
        Assert.Equal(0, arbiter.PhaseIndex);
        Assert.Same(arbiter.Active, arbiter.EffectiveIntent);
        arbiter.Update(Fx.Features(131));

        // The same playbook again is a fresh activation, so the plan starts over from phase 0.
        Activate("c", "test-expand-tech-attack", StrategicPosture.Expand, 200);
        Assert.Equal("expand", arbiter.PhaseName);
        arbiter.Update(Fx.Features(201));
        Assert.Equal("attack", arbiter.PhaseName);
        Assert.Equal(("expand", "attack"), Changes()[^1]);
    }

    [Fact]
    public void A_renewal_keeps_the_current_phase()
    {
        Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        arbiter.Update(Fx.Features(65));
        StrategicIntent renewal = Fx.Intent("a2", "test-expand-tech-attack", StrategicPosture.Expand, issuedAt: 70, lifetime: 1000, budget: StartBudget);
        Assert.Equal(ArbitrationOutcome.Renewed, arbiter.Offer(Fx.Accepted(renewal), Fx.Features(70)).Outcome);
        Assert.Equal("tech", arbiter.PhaseName);
        Assert.Equal("a2", arbiter.EffectiveIntent!.IntentId);
        Assert.Equal(TechBudget, arbiter.EffectiveIntent.Budget);
    }

    [Fact]
    public void A_playbook_without_phases_is_untouched()
    {
        StrategicIntent intent = Activate("a", "allied-boom", StrategicPosture.Boom, 0);
        arbiter.Update(Fx.Features(500));
        Assert.Null(arbiter.PhaseName);
        Assert.Same(intent, arbiter.EffectiveIntent);
        Assert.Empty(log.OfKind(DecisionRecordKinds.PhaseChanged));
    }

    [Fact]
    public void An_abort_still_ends_a_phased_intent_and_drops_the_phase()
    {
        StrategicIntent intent = Fx.Intent("a", "test-expand-tech-attack", StrategicPosture.Expand, lifetime: 1000, budget: StartBudget,
            abort: [new Condition(ConditionMetric.GameSeconds, Comparison.Ge, 100)]);
        arbiter.Offer(Fx.Accepted(intent), Fx.Features(0));
        arbiter.Update(Fx.Features(60));
        Assert.Equal("tech", arbiter.PhaseName);
        arbiter.Update(Fx.Features(100));
        Assert.Null(arbiter.Active);
        Assert.Null(arbiter.EffectiveIntent);
        Assert.Null(arbiter.PhaseName);
    }

    [Fact]
    public void The_phase_changed_record_names_intent_playbook_phases_and_frame()
    {
        Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        StrategicFeatures at60 = Fx.Features(60);
        arbiter.Update(at60);
        DecisionRecord record = Assert.Single(log.OfKind(DecisionRecordKinds.PhaseChanged));
        Assert.Equal("strategy.phase_changed", record.Kind);
        Assert.Equal("a", record.Data.GetProperty("intentId").GetString());
        Assert.Equal("test-expand-tech-attack", record.Data.GetProperty("playbookId").GetString());
        Assert.Equal("expand", record.Data.GetProperty("fromPhase").GetString());
        Assert.Equal("tech", record.Data.GetProperty("toPhase").GetString());
        Assert.Equal(at60.Time.Frame, record.Data.GetProperty("frame").GetInt64());
    }

    // ---- JSON and validation -------------------------------------------------------------------------------------

    [Fact]
    public void A_phased_playbook_round_trips_through_the_document_schema()
    {
        string json = JsonSerializer.Serialize(new PlaybookDocument([ExpandTechAttack()]), BotJson.Options);
        Assert.Contains("\"phases\"", json, StringComparison.Ordinal);

        PlaybookLibrary loaded = PlaybookLibrary.LoadJson(json);

        Assert.True(loaded.TryGet("test-expand-tech-attack", out Playbook playbook));
        Assert.Equal(["expand", "tech", "attack"], playbook.Phases!.Select(static p => p.Name));
        Assert.Equal(TechBudget, playbook.Phases![1].Budget);
        Assert.Null(playbook.Phases[1].Composition);
        Assert.Equal(StrategicPosture.Pressure, playbook.Phases[2].Posture);
        Assert.Equal(new Condition(ConditionMetric.GameSeconds, Comparison.Ge, 120), Assert.Single(playbook.Phases[2].EnterWhen));
        Assert.Empty(playbook.Phases[0].EnterWhen);
    }

    [Fact]
    public void A_playbook_without_phases_loads_with_null_phases_and_the_default_set_has_none()
    {
        Assert.All(PlaybookLibrary.LoadDefault().All, static p => Assert.Null(p.Phases));
        string json = JsonSerializer.Serialize(new PlaybookDocument([Fx.Playbooks.All[0]]), BotJson.Options);
        Assert.Null(PlaybookLibrary.LoadJson(json).All[0].Phases);
    }

    private static string Load(Playbook playbook) =>
        Assert.Throws<InvalidDataException>(() => PlaybookLibrary.LoadJson(JsonSerializer.Serialize(new PlaybookDocument([playbook]), BotJson.Options))).Message;

    [Fact]
    public void Duplicate_phase_names_are_rejected()
    {
        Playbook p = ExpandTechAttack();
        string message = Load(p with { Phases = [p.Phases![0], p.Phases[1], p.Phases[2] with { Name = "tech" }] });
        Assert.Contains("'tech' more than once", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_phase_budget_is_rejected_by_the_playbook_budget_rule()
    {
        Playbook p = ExpandTechAttack();
        string message = Load(p with { Phases = [p.Phases![0], p.Phases[1] with { Budget = new BudgetShares(0.5, 0.5, 0.5, 0) }] });
        Assert.Contains("phase 'tech' budget shares", message, StringComparison.Ordinal);
        message = Load(p with { Phases = [p.Phases![0], p.Phases[1] with { Budget = new BudgetShares(1.5, -0.5, 0, 0) }] });
        Assert.Contains("phase 'tech' budget shares", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_phase_must_have_no_enter_conditions()
    {
        Playbook p = ExpandTechAttack();
        string message = Load(p with { Phases = [p.Phases![0] with { EnterWhen = p.Phases[1].EnterWhen }, p.Phases[2]] });
        Assert.Contains("start phase", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phase_with_no_name_or_null_enter_conditions_is_rejected()
    {
        Playbook p = ExpandTechAttack();
        Assert.Contains("no name", Load(p with { Phases = [p.Phases![0], p.Phases[1] with { Name = " " }] }), StringComparison.Ordinal);
        Assert.Contains("enter condition", Load(p with { Phases = [p.Phases![0], p.Phases[1] with { EnterWhen = null! }] }), StringComparison.Ordinal);
    }

    // ---- Review fixes: attack threshold, mirrored proposals, validation ------------------------------------------------

    private static Playbook WithAttackThreshold() => ExpandTechAttack() with
    {
        Id = "test-threshold",
        Parameters = [new PlaybookParameter("attackArmyValue", 500, 8000, 3000, "Army value before attacking.")],
        Phases =
        [
            new PlaybookPhase("expand", []),
            new PlaybookPhase("attack", [new Condition(ConditionMetric.GameSeconds, Comparison.Ge, 60)],
                AttackConditions:
                [
                    new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 3000),   // tracks the parameter
                    new Condition(ConditionMetric.OwnArmyValue, Comparison.Ge, 777),    // set on its own account
                    new Condition(ConditionMetric.ArmyValueRatio, Comparison.Gt, 0.1),
                ]),
        ],
    };

    private static double[] OwnArmyBounds(StrategicIntent intent) =>
        intent.AttackConditions.Where(static c => c.Metric == ConditionMetric.OwnArmyValue).Select(static c => c.Threshold).ToArray();

    [Theory]
    [InlineData(3000, new[] { 3000d, 777d })]   // the default: nothing moves
    [InlineData(1500, new[] { 1500d, 777d })]   // lowered: the bound that tracked the default follows, the other stays
    public void A_phase_attack_condition_follows_the_intents_attack_army_value_parameter(double parameter, double[] expected)
    {
        IntentArbiter own = new(new PlaybookLibrary([WithAttackThreshold()]), null, log);
        StrategicIntent intent = Fx.Intent("a", "test-threshold", StrategicPosture.Expand, lifetime: 1000, budget: StartBudget,
            parameters: new Dictionary<string, double> { ["attackArmyValue"] = parameter });
        own.Offer(Fx.Accepted(intent), Fx.Features(0));
        own.Update(Fx.Features(60));

        Assert.Equal("attack", own.PhaseName);
        Assert.Equal(expected, OwnArmyBounds(own.EffectiveIntent!));
    }

    [Fact]
    public void A_refined_parameter_moves_the_phase_attack_bound_on_the_next_effective_intent()
    {
        IntentArbiter own = new(new PlaybookLibrary([WithAttackThreshold()]), null, log);
        own.Offer(Fx.Accepted(Fx.Intent("a", "test-threshold", StrategicPosture.Expand, lifetime: 1000, budget: StartBudget)), Fx.Features(0));
        own.Update(Fx.Features(60));
        Assert.Equal(3000, OwnArmyBounds(own.EffectiveIntent!)[0]);

        StrategicIntent renewal = Fx.Intent("a2", "test-threshold", StrategicPosture.Expand, issuedAt: 70, lifetime: 1000, budget: StartBudget,
            parameters: new Dictionary<string, double> { ["attackArmyValue"] = 2000 });
        Assert.Equal(ArbitrationOutcome.Renewed, own.Offer(Fx.Accepted(renewal), Fx.Features(70)).Outcome);
        Assert.Equal("attack", own.PhaseName);
        Assert.Equal(2000, OwnArmyBounds(own.EffectiveIntent!)[0]);
    }

    private StrategicIntent Challenger(string id, string playbook, StrategicPosture posture, double at, BudgetShares? budget = null, double confidence = 0.95) =>
        Fx.Intent(id, playbook, posture, issuedAt: at, lifetime: 1000, budget: budget ?? StartBudget, confidence: confidence);

    [Fact]
    public void A_proposal_that_mirrors_the_phase_in_force_is_a_renewal_that_keeps_the_phase()
    {
        Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        arbiter.Update(Fx.Features(65));
        Assert.Equal("tech", arbiter.PhaseName);
        Assert.Equal(StrategicPosture.Tech, arbiter.EffectiveIntent!.Posture);

        // The model saw the effective plan (posture Tech, the tech budget) and echoes it back.
        ArbitrationDecision decision = arbiter.Offer(Fx.Accepted(Challenger("m", "test-expand-tech-attack", StrategicPosture.Tech, 70, TechBudget)), Fx.Features(70));

        Assert.Equal(ArbitrationOutcome.Renewed, decision.Outcome);
        Assert.Equal("m", arbiter.Active!.IntentId);
        Assert.Equal("tech", arbiter.PhaseName);
        // The echoed fields are folded back to the intent's own values, so later phases still start from them.
        Assert.Equal(StrategicPosture.Expand, arbiter.Active.Posture);
        Assert.Equal(StartBudget, arbiter.Active.Budget);
        Assert.Equal(StrategicPosture.Tech, arbiter.EffectiveIntent!.Posture);
        Assert.Equal(TechBudget, arbiter.EffectiveIntent.Budget);
        Assert.Single(log.OfKind(DecisionRecordKinds.IntentActivated), static r => !r.Data.GetProperty("renewal").GetBoolean());
        Assert.Single(Changes());

        arbiter.Update(Fx.Features(125));
        Assert.Equal("attack", arbiter.PhaseName);
    }

    [Fact]
    public void A_mirroring_proposal_keeps_a_field_of_its_own_that_differs_from_the_phase()
    {
        Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        arbiter.Update(Fx.Features(65));
        BudgetShares own = new(0.25, 0.25, 0.4, 0.1);
        arbiter.Offer(Fx.Accepted(Challenger("m", "test-expand-tech-attack", StrategicPosture.Tech, 70, own)), Fx.Features(70));
        Assert.Equal(own, arbiter.Active!.Budget);
        Assert.Equal(TechBudget, arbiter.EffectiveIntent!.Budget);   // the phase's override still wins while it is in force
    }

    [Fact]
    public void A_different_posture_or_playbook_is_still_a_switch_that_restarts_the_plan()
    {
        Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        arbiter.Update(Fx.Features(65));

        // Neither the intent's own posture nor the phase's: a real posture change.
        ArbitrationDecision posture = arbiter.Offer(Fx.Accepted(Challenger("p", "test-expand-tech-attack", StrategicPosture.Boom, 70)), Fx.Features(70));
        Assert.Equal(ArbitrationOutcome.Activated, posture.Outcome);
        Assert.Equal("expand", arbiter.PhaseName);   // fresh activation: phase 0

        // At phase 0 the phase's posture is not in force, so echoing it is a posture change too.
        ArbitrationDecision atStart = arbiter.Offer(Fx.Accepted(Challenger("q", "test-expand-tech-attack", StrategicPosture.Tech, 120)), Fx.Features(120));
        Assert.NotEqual("renewal", atStart.Reason);
    }

    [Fact]
    public void The_active_intent_prompt_names_the_phase_and_its_effective_overrides()
    {
        StrategicIntent active = ClaudeFixtures.ActiveIntent();
        StrategicIntent effective = active with { Posture = StrategicPosture.Tech, Budget = TechBudget };
        StrategistContext context = ClaudeFixtures.Context(ClaudeFixtures.Features(seconds: 300), active) with { Phase = new ActivePhase("tech", 1, 3, effective) };
        using JsonDocument situation = JsonDocument.Parse(new IntentPromptBuilder().Build(context, StrategistMode.Strategic).Situation);
        JsonElement intent = situation.RootElement.GetProperty("activeIntent");

        Assert.Equal("Boom", intent.GetProperty("posture").GetString());   // the intent's own value
        JsonElement phase = intent.GetProperty("phase");
        Assert.Equal("tech", phase.GetProperty("name").GetString());
        Assert.Equal(1, phase.GetProperty("index").GetInt32());
        Assert.Equal(3, phase.GetProperty("count").GetInt32());
        Assert.Equal("Tech", phase.GetProperty("effectivePosture").GetString());
        Assert.Equal(0.5, phase.GetProperty("effectiveBudget").GetProperty("tech").GetDouble());

        StrategistContext plain = ClaudeFixtures.Context(ClaudeFixtures.Features(seconds: 300), active);
        using JsonDocument without = JsonDocument.Parse(new IntentPromptBuilder().Build(plain, StrategistMode.Strategic).Situation);
        Assert.False(without.RootElement.GetProperty("activeIntent").TryGetProperty("phase", out _));
    }

    [Fact]
    public void The_arbiter_supplies_the_phase_for_strategist_contexts()
    {
        Assert.Null(arbiter.CurrentPhase);
        Activate("a", "test-expand-tech-attack", StrategicPosture.Expand, 0);
        Assert.Equal("expand", arbiter.CurrentPhase!.Name);
        arbiter.Update(Fx.Features(65));
        ActivePhase phase = arbiter.CurrentPhase!;
        Assert.Equal(("tech", 1, 3), (phase.Name, phase.Index, phase.Count));
        Assert.Equal(TechBudget, phase.Effective.Budget);
        Activate("b", "allied-turtle", StrategicPosture.Turtle, 130);
        Assert.Null(arbiter.CurrentPhase);
    }

    [Fact]
    public void A_phase_composition_is_checked_like_a_proposed_one()
    {
        Playbook p = ExpandTechAttack();
        PlaybookPhase tech = p.Phases![1];
        string Bad(IReadOnlyList<CompositionTarget> composition) => Load(p with { Phases = [p.Phases[0], tech with { Composition = composition }] });

        Assert.Contains("more than once", Bad([new(UnitRole.AntiArmor, 0.1, 0.5), new(UnitRole.AntiArmor, 0.1, 0.5)]), StringComparison.Ordinal);
        Assert.Contains("not a range", Bad([new(UnitRole.AntiArmor, 0.6, 0.5)]), StringComparison.Ordinal);
        Assert.Contains("not a range", Bad([new(UnitRole.AntiArmor, 0.1, 1.5)]), StringComparison.Ordinal);
        Assert.Contains("Minimum shares sum", Bad([new(UnitRole.AntiArmor, 0.6, 0.9), new(UnitRole.AntiAir, 0.6, 0.9)]), StringComparison.Ordinal);
        Assert.Contains("phase 'tech'", Bad([new(UnitRole.AntiArmor, 0.6, 0.5)]), StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_conditions_are_checked_like_a_proposed_ones()
    {
        Playbook p = ExpandTechAttack();
        PlaybookPhase tech = p.Phases![1];
        Condition nan = new(ConditionMetric.OwnArmyValue, Comparison.Ge, double.NaN);
        Condition noRegion = new(ConditionMetric.LocalForceRatio, Comparison.Gt, 1);

        Assert.Contains("NaN threshold", Load(p with { Phases = [p.Phases[0], tech with { EnterWhen = [nan] }] }), StringComparison.Ordinal);
        Assert.Contains("NaN threshold", Load(p with { Phases = [p.Phases[0], tech with { AttackConditions = [nan] }] }), StringComparison.Ordinal);
        Assert.Contains("needs a region", Load(p with { Phases = [p.Phases[0], tech with { EnterWhen = [noRegion] }] }), StringComparison.Ordinal);
        Assert.Contains("needs a region", Load(p with { Phases = [p.Phases[0], tech with { AttackConditions = [noRegion] }] }), StringComparison.Ordinal);

        // A region on a region-scoped condition loads; that it exists on the map is not knowable here and is not checked.
        Condition scoped = new(ConditionMetric.LocalForceRatio, Comparison.Gt, 1, new RegionId(9999));
        Assert.NotNull(PlaybookLibrary.LoadJson(JsonSerializer.Serialize(new PlaybookDocument([p with { Phases = [p.Phases[0], tech with { EnterWhen = [scoped] }] }]), BotJson.Options)));
    }

    // ---- A real runtime -------------------------------------------------------------------------------------------

    private static PlaybookLibrary MatchLibrary() => new([.. MatchHarness.Playbooks.All, ExpandTechAttack()]);

    private static MatchHarness PhasedMatch(IStrategist? primary = null, BotOptions? options = null) =>
        MatchHarness.Create(
            primary ?? new PinnedPlaybookStrategist(new Dictionary<Faction, string> { [Faction.Allied] = "test-expand-tech-attack" }, "pinned-phased", double.PositiveInfinity),
            "balanced", seed: 1, armOptions: options, armPlaybooks: MatchLibrary());

    [Fact]
    public void A_phase_transition_changes_the_budget_shares_the_operational_planner_and_ledger_work_from()
    {
        // Non-accruing ledger: each period's pool caps are exactly the effective intent's shares of capacity.
        using MatchHarness match = PhasedMatch(options: StandardBot.SimulatorOptions with { AccrueBudgets = false });
        double? armyShareBefore = null;
        double? armyShareAfter = null;
        match.RunUntil(140, () =>
        {
            if (match.Arm.Ledger.Capacity <= 0 || match.Arm.ActiveIntent is null) return;
            double share = match.Arm.Ledger.PoolCapacity(BudgetPools.Army) / (double)match.Arm.Ledger.Capacity;
            double seconds = match.Sim.Time.Seconds;
            if (seconds is > 30 and < 55) armyShareBefore = share;
            if (seconds > 125) armyShareAfter = share;
        });

        Assert.Equal("test-expand-tech-attack", match.Arm.ActiveIntent?.PlaybookId);
        Assert.Equal("attack", match.Arm.Arbiter.PhaseName);
        Assert.NotNull(armyShareBefore);
        Assert.NotNull(armyShareAfter);
        Assert.InRange(armyShareBefore!.Value, 0.05, 0.2);   // the intent's own budget: army 0.1
        Assert.InRange(armyShareAfter!.Value, 0.7, 0.85);    // attack phase: army 0.8
        Assert.Equal(2, match.ArmLog.OfKind(DecisionRecordKinds.PhaseChanged).Count);
        // The plan is rebuilt on the transition, not left for the next cadence.
        foreach (DecisionRecord change in match.ArmLog.OfKind(DecisionRecordKinds.PhaseChanged))
        {
            Assert.Contains(match.ArmLog.OfKind(DecisionRecordKinds.Plan), plan => plan.Time.Frame - change.Time.Frame is >= 0 and <= 1);
        }
    }

    [Fact]
    public void Replay_reproduces_phase_changed_records_and_the_whole_decision_log()
    {
        using MatchHarness recorded = PhasedMatch();
        recorded.RunUntil(140);
        IReadOnlyList<DecisionRecord> records = DecisionLogCodec.ReadAll(new StringReader(recorded.ArmLog.ToNdjson()));
        Assert.Equal(2, records.Count(static r => r.Kind == DecisionRecordKinds.PhaseChanged));

        using MatchHarness replayed = PhasedMatch(new ReplayStrategist(records));
        replayed.RunUntil(140);

        Assert.Equal(DecisionLogCodec.Hash(records), DecisionLogCodec.Hash(replayed.ArmLog.Records));
        Assert.Equal(2, replayed.ArmLog.OfKind(DecisionRecordKinds.PhaseChanged).Count);
    }
}
