// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Sim.Opponents;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Opponents;

/// <summary>
/// The independent scripted opponent: it must actually play (base, harvesters, waves), respect fog, be
/// deterministic per seed, and at hard difficulty beat a player that does nothing — otherwise arena results
/// against it say nothing about the bot.
/// </summary>
public sealed class ScriptedSkirmishAiTests
{
    private static readonly PlayerId Ai = new(0);
    private static readonly PlayerId Idle = new(1);
    private static readonly IRulesDatabase Rules = RulesDatabase.LoadEmbeddedFixture();

    private static SkirmishSimulation NewSim(int seed, Faction faction, double maxSeconds) =>
        new(SimMaps.TwinValley, Rules, new SimSettings(seed, maxSeconds,
            [new SimPlayer(Ai, faction), new SimPlayer(Idle, faction == Faction.Allied ? Faction.Soviet : Faction.Allied)]));

    private static List<string> Play(SkirmishSimulation sim, ScriptedSkirmishAi ai, double seconds)
    {
        List<string> trace = [];
        while (!sim.MatchEnded && sim.Time.Seconds < seconds)
        {
            foreach (GameCommand command in ai.Tick(sim.Observe(Ai)))
            {
                trace.Add($"{sim.Time.Frame}:{command}");
                sim.Submit(Ai, command);
            }
            sim.Step();
        }
        return trace;
    }

    [Theory]
    [InlineData("ai-rush", Faction.Allied)]
    [InlineData("ai-balanced", Faction.Soviet)]
    [InlineData("ai-turtle", Faction.Allied)]
    [InlineData("ai-air", Faction.Soviet)]
    public void Builds_a_base_with_harvesters_and_launches_attack_waves(string style, Faction faction)
    {
        SkirmishSimulation sim = NewSim(3, faction, 900);
        ScriptedSkirmishAi ai = new(Rules, Ai, faction, sim.Map, style, OpponentDifficulty.Hard, 3);

        Play(sim, ai, 800);

        List<ObservedEntity> own = [.. sim.Observe(Ai, ObservationMode.Oracle).Entities.Where(e => e.Owner == Ai)];
        List<UnitRule> rules = [.. own.Select(e => Rules.Get(e.TypeId))];
        Assert.Contains(rules, r => r.Role == UnitRole.Economy);
        Assert.Contains(rules, r => r.Role == UnitRole.Harvester);
        Assert.True(rules.Count(r => r.Kind == EntityKind.Building && r.Role == UnitRole.Production) >= 3, "yard, barracks and war factory");
        Assert.True(ai.WavesLaunched >= 1, $"{style} launched no attack wave by 800 s");
        Assert.Equal(0, sim.RejectedCommandCount(Ai));
    }

    [Fact]
    public void Air_style_builds_aircraft()
    {
        SkirmishSimulation sim = NewSim(5, Faction.Allied, 900);
        ScriptedSkirmishAi ai = new(Rules, Ai, Faction.Allied, sim.Map, "ai-air", OpponentDifficulty.Hard, 5);
        Play(sim, ai, 700);
        Assert.Contains(sim.Observe(Ai, ObservationMode.Oracle).Entities, e => e.Owner == Ai && Rules.Get(e.TypeId).Kind == EntityKind.Aircraft);
    }

    [Fact]
    public void Hard_beats_a_player_that_does_nothing()
    {
        SkirmishSimulation sim = NewSim(7, Faction.Soviet, 1500);
        ScriptedSkirmishAi ai = new(Rules, Ai, Faction.Soviet, sim.Map, "ai-balanced", OpponentDifficulty.Hard, 7);
        Play(sim, ai, 1500);
        Assert.True(sim.MatchEnded);
        Assert.Equal(Ai, sim.Winner);
        Assert.Equal("elimination", sim.EndReason);
    }

    [Fact]
    public void Same_seed_same_commands_and_difficulty_changes_only_timing_and_size()
    {
        List<string> a = Play(NewSim(11, Faction.Allied, 600), new(Rules, Ai, Faction.Allied, SimMaps.TwinValley.Map, "ai-balanced", OpponentDifficulty.Hard, 11), 600);
        List<string> b = Play(NewSim(11, Faction.Allied, 600), new(Rules, Ai, Faction.Allied, SimMaps.TwinValley.Map, "ai-balanced", OpponentDifficulty.Hard, 11), 600);
        Assert.Equal(a, b);

        SkirmishSimulation easySim = NewSim(11, Faction.Allied, 600);
        ScriptedSkirmishAi easy = new(Rules, Ai, Faction.Allied, easySim.Map, "ai-balanced", OpponentDifficulty.Easy, 11);
        Play(easySim, easy, 470);
        Assert.Equal(0, easy.WavesLaunched);
    }

    [Fact]
    public void Commands_do_not_depend_on_hidden_enemy_state()
    {
        SkirmishSimulation plain = NewSim(13, Faction.Allied, 400);
        SkirmishSimulation perturbed = NewSim(13, Faction.Allied, 400);
        ScriptedSkirmishAi aiPlain = new(Rules, Ai, Faction.Allied, plain.Map, "ai-rush", OpponentDifficulty.Hard, 13);
        ScriptedSkirmishAi aiPerturbed = new(Rules, Ai, Faction.Allied, perturbed.Map, "ai-rush", OpponentDifficulty.Hard, 13);
        Play(plain, aiPlain, 120);
        Play(perturbed, aiPerturbed, 120);
        Assert.Equal(plain.ComputeStateHash(), perturbed.ComputeStateHash());

        SimLeakageProbe.PerturbHidden(perturbed, Ai);
        Assert.NotEqual(plain.ComputeStateHash(), perturbed.ComputeStateHash());

        int compared = 0;
        while (plain.Time.Seconds < 180)
        {
            ObservationFrame fa = plain.Observe(Ai), fb = perturbed.Observe(Ai);
            if (fa.Entities.Count != fb.Entities.Count || fa.Credits != fb.Credits) break;
            IReadOnlyList<GameCommand> ca = aiPlain.Tick(fa), cb = aiPerturbed.Tick(fb);
            Assert.Equal(ca.Select(c => c.ToString()), cb.Select(c => c.ToString()));
            foreach (GameCommand c in ca) plain.Submit(Ai, c);
            foreach (GameCommand c in cb) perturbed.Submit(Ai, c);
            plain.Step();
            perturbed.Step();
            compared++;
        }
        Assert.True(compared > 100, $"only {compared} frames compared");
    }

    [Theory]
    [InlineData("ai-rush", true)]
    [InlineData("ai-air:easy", true)]
    [InlineData("ai-turtle:medium", true)]
    [InlineData("rush", false)]
    [InlineData("ai-rush:insane", false)]
    public void Parses_style_and_difficulty(string name, bool valid)
    {
        Assert.Equal(valid, OpponentProfiles.TryParse(name, out _, out _));
    }
}
