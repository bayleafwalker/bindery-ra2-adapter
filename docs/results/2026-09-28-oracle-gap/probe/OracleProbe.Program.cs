using System.Text.Json;
using Bindery.Ra2.Bot;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Belief;
using Bindery.Ra2.Bot.Features;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;
using Bindery.Ra2.Bot.Arbitration;

// For each match the arm plays in mode M (belief or oracle); every frame a shadow BeliefModel+FeatureCompiler is fed the
// SAME-mode frame (control: must equal the arm's own features) and another the OTHER-mode frame. Both compiled; compare.
var (rules, playbooks, _) = Bindery.Ra2.Bot.Arena.Program.LoadRules(null);
var ctx = new ArenaRunContext(true, null);
var factory = new BotAgentFactory(rules, playbooks, ctx);
string[] opps = ["ai-horde:easy","ai-horde:medium","ai-horde:hard","ai-armor:easy","ai-armor:medium","ai-armor:hard"];
int seeds = int.Parse(args.Length > 0 ? args[0] : "8");
string only = args.Length > 1 ? args[1] : "both";
var selector = new PlaybookSelector();
var json = new JsonSerializerOptions { WriteIndented = false };
string J(object? o) => JsonSerializer.Serialize(o, json);
string Rule(StrategicFeatures f) { var (p, r, _) = selector.Choose(f, playbooks, rules); return (p?.Id ?? "-") + "|" + System.Text.RegularExpressions.Regex.Replace(r, @"[-0-9.]+", "#"); }
double Air(StrategicFeatures f) => Math.Min(1.0, 0.3 * f.Enemy.KnownTech.Count(t => rules.TryGet(t, out UnitRule u) && u.Kind == EntityKind.Aircraft));
double Armour(StrategicFeatures f) { double tot = f.Enemy.CompositionByRole.Values.Where(v => v > 0).Sum(); return tot <= 0 ? 0 : f.Enemy.CompositionByRole.GetValueOrDefault(UnitRole.AntiArmor) / tot; }
Dictionary<string,string> Diff(object a, object b) { var d = new Dictionary<string,string>(); foreach (var pr in a.GetType().GetProperties()) { if (pr.Name=="EqualityContract") continue; string x = J(pr.GetValue(a)), y = J(pr.GetValue(b)); if (x != y) d[pr.Name] = (x.Length>300?x[..300]:x) + " || " + (y.Length>300?y[..300]:y); } return d; }
using var outw = new StreamWriter(Console.OpenStandardOutput());
foreach (bool armOracle in new[] { false, true })
{
  if (only == "belief" && armOracle) continue; if (only == "oracle" && !armOracle) continue;
  foreach (SimMap map0 in SimMaps.HeldOut)
  foreach (string opp in opps)
  for (int seed = 1; seed <= seeds; seed++)
  {
    var arm = new ArmSpec("selector", armOracle, true);
    Faction af = MatchRunner.ArmFaction(seed); Faction of = af == Faction.Allied ? Faction.Soviet : Faction.Allied;
    SimSettings settings = BenchmarkSettings.Contested.ToSimSettings(seed, 1200, MatchRunner.ArmPlayer, af, MatchRunner.OpponentPlayer, of, OpponentSets.IncomeHandicap(opp));
    var sim = new SkirmishSimulation(MatchRunner.Oriented(map0, seed), rules, settings);
    using IArenaAgent a = factory.Create(arm, MatchRunner.ArmPlayer, af, sim.Map, seed);
    using IArenaAgent o = factory.Create(new ArmSpec(opp, false, false), MatchRunner.OpponentPlayer, of, sim.Map, seed);
    var fopt = Bindery.Ra2.Bot.Tuning.TunedParameterSet.Active.ApplyTo(new FeatureOptions());
    var sameB = new BeliefModel(rules, new BeliefOptions()); var sameF = new FeatureCompiler(rules, fopt);
    var othB = new BeliefModel(rules, new BeliefOptions()); var othF = new FeatureCompiler(rules, fopt);
    var botAgent = (BotArenaAgent)a;
    int maxFrames = 1200 * GameTime.FramesPerSecond + GameTime.FramesPerSecond;
    int ctlMismatch = 0, samples = 0;
    for (int frame = 0; frame < maxFrames && !sim.MatchEnded; frame++)
    {
      var mSame = armOracle ? ObservationMode.Oracle : ObservationMode.Belief;
      var mOth = armOracle ? ObservationMode.Belief : ObservationMode.Oracle;
      var fa = sim.Observe(MatchRunner.ArmPlayer, mSame);
      var fOth = sim.Observe(MatchRunner.ArmPlayer, mOth);
      var fo = sim.Observe(MatchRunner.OpponentPlayer, ObservationMode.Belief);
      var cmds = a.Tick(fa);
      var fs = sameF.Compile(sameB.Apply(fa));
      var fx = othF.Compile(othB.Apply(fOth));
      foreach (var c in cmds) sim.Submit(MatchRunner.ArmPlayer, c);
      foreach (var c in o.Tick(fo)) sim.Submit(MatchRunner.OpponentPlayer, c);
      sim.Step();
      if (frame % (GameTime.FramesPerSecond * 5) != 0) continue;
      samples++;
      var actual = botAgent.Runtime.CurrentFeatures!;
      if (J(actual.Enemy) != J(fs.Enemy) || J(actual.Army) != J(fs.Army) || J(actual.Threats) != J(fs.Threats)) ctlMismatch++;
      var bel = armOracle ? fx : fs; var ora = armOracle ? fs : fx;
      var rec = new Dictionary<string, object?> {
        ["armOracle"] = armOracle, ["map"] = map0.Map.MapId, ["opp"] = opp, ["seed"] = seed, ["faction"] = af.ToString(), ["t"] = bel.Time.Seconds,
        ["econEq"] = J(bel.Economy) == J(ora.Economy), ["armyEq"] = J(bel.Army) == J(ora.Army), ["econDiff"] = Diff(bel.Economy, ora.Economy), ["armyDiff"] = Diff(bel.Army, ora.Army),
        ["ownValB"] = bel.Army.ArmyValue.Current, ["ownValO"] = ora.Army.ArmyValue.Current,
        ["enB"] = bel.Enemy.EstimatedArmyValue.Current, ["enO"] = ora.Enemy.EstimatedArmyValue.Current,
        ["confB"] = bel.Enemy.ArmyValueConfidence, ["confO"] = ora.Enemy.ArmyValueConfidence,
        ["ratioB"] = ConditionEvaluator.ArmyValueRatio(bel), ["ratioO"] = ConditionEvaluator.ArmyValueRatio(ora),
        ["threatB"] = ConditionEvaluator.BaseThreatRatio(bel), ["threatO"] = ConditionEvaluator.BaseThreatRatio(ora),
        ["armB"] = Armour(bel), ["armO"] = Armour(ora), ["airB"] = Air(bel), ["airO"] = Air(ora),
        ["ruleB"] = Rule(bel), ["ruleO"] = Rule(ora), ["ruleActual"] = Rule(actual),
        ["compB"] = bel.Enemy.CompositionByRole, ["compO"] = ora.Enemy.CompositionByRole,
        ["techB"] = bel.Enemy.KnownTech.Count, ["techO"] = ora.Enemy.KnownTech.Count,
        ["nThrB"] = bel.Threats.Count, ["nThrO"] = ora.Threats.Count,
      };
      outw.WriteLine(J(rec));
    }
    outw.WriteLine(J(new Dictionary<string, object?> { ["summary"] = true, ["armOracle"] = armOracle, ["map"] = map0.Map.MapId, ["opp"] = opp, ["seed"] = seed, ["winner"] = sim.Winner?.Value, ["ctlMismatch"] = ctlMismatch, ["samples"] = samples }));
  }
}
