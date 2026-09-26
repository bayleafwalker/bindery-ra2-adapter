// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Sim;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Tests.Integration;

/// <summary>
/// A real match: the region simulator with the committed rules fixture and default
/// playbooks, a <see cref="BotRuntime"/> for the arm under test (player 0, Allied)
/// and one for a pinned-playbook opponent (player 1, Soviet), stepped frame by frame
/// exactly as the arena does.
/// </summary>
internal sealed class MatchHarness : IDisposable
{
    public static readonly PlayerId ArmPlayer = new(0);
    public static readonly PlayerId OpponentPlayer = new(1);

    public static IRulesDatabase Rules { get; } = RulesDatabase.LoadEmbeddedFixture();

    public static IPlaybookLibrary Playbooks { get; } = PlaybookLibrary.LoadDefault();

    public static IReadOnlyDictionary<string, IReadOnlyDictionary<Faction, string>> Styles { get; } =
        new Dictionary<string, IReadOnlyDictionary<Faction, string>>(StringComparer.Ordinal)
        {
            ["rush"] = new Dictionary<Faction, string> { [Faction.Allied] = "allied-grizzly-timing", [Faction.Soviet] = "soviet-rhino-rush" },
            ["balanced"] = new Dictionary<Faction, string> { [Faction.Allied] = "allied-ifv-mix", [Faction.Soviet] = "soviet-flak-mix" },
            ["turtle"] = new Dictionary<Faction, string> { [Faction.Allied] = "allied-prism-turtle", [Faction.Soviet] = "soviet-turtle" },
        };

    private MatchHarness(SkirmishSimulation sim, BotRuntime arm, DecisionLog armLog, BotRuntime? opponent, Bindery.Ra2.Bot.Sim.Opponents.ScriptedSkirmishAi? scripted = null)
    {
        Sim = sim;
        Arm = arm;
        ArmLog = armLog;
        Opponent = opponent;
        Scripted = scripted;
    }

    /// <summary>A match against the independent scripted opponent (<c>ai-*</c> style at hard difficulty) instead of a pinned playbook.</summary>
    public static MatchHarness CreateVsScripted(IStrategist armPrimary, string aiStyle, SimMap? map = null, int seed = 1, double maxSeconds = 1200)
    {
        SimSettings settings = new(seed, maxSeconds, [new SimPlayer(ArmPlayer, Faction.Allied), new SimPlayer(OpponentPlayer, Faction.Soviet)]);
        SkirmishSimulation sim = new(map ?? SimMaps.TwinValley, Rules, settings);
        DecisionLog armLog = new();
        BotRuntime arm = StandardBot.Create(Rules, Playbooks, armPrimary, log: armLog);
        Bindery.Ra2.Bot.Sim.Opponents.ScriptedSkirmishAi scripted = new(Rules, OpponentPlayer, Faction.Soviet, sim.Map, aiStyle, Bindery.Ra2.Bot.Sim.Opponents.OpponentDifficulty.Hard, seed);
        return new MatchHarness(sim, arm, armLog, null, scripted);
    }

    public Bindery.Ra2.Bot.Sim.Opponents.ScriptedSkirmishAi? Scripted { get; }

    public SkirmishSimulation Sim { get; }

    public BotRuntime Arm { get; }

    public DecisionLog ArmLog { get; }

    public BotRuntime? Opponent { get; }

    public List<DroppedCommand> ArmDropped { get; } = [];

    public static MatchHarness Create(
        IStrategist armPrimary,
        string opponentStyle = "rush",
        SimMap? map = null,
        int seed = 1,
        double maxSeconds = 1200,
        IStrategist? armFallback = null,
        BotOptions? armOptions = null)
    {
        SimSettings settings = new(seed, maxSeconds, [new SimPlayer(ArmPlayer, Faction.Allied), new SimPlayer(OpponentPlayer, Faction.Soviet)]);
        SkirmishSimulation sim = new(map ?? SimMaps.TwinValley, Rules, settings);
        DecisionLog armLog = new();
        BotRuntime arm = StandardBot.Create(Rules, Playbooks, armPrimary, armFallback, log: armLog, options: armOptions);
        BotRuntime opponent = StandardBot.Create(Rules, Playbooks, new PinnedPlaybookStrategist(Styles[opponentStyle], $"style-{opponentStyle}"));
        return new MatchHarness(sim, arm, armLog, opponent);
    }

    public void Frame()
    {
        ObservationFrame armFrame = Sim.Observe(ArmPlayer);
        ObservationFrame opponentFrame = Sim.Observe(OpponentPlayer);
        foreach (GameCommand command in Arm.Tick(armFrame)) Sim.Submit(ArmPlayer, command);
        ArmDropped.AddRange(Arm.LastDropped);
        IReadOnlyList<GameCommand> opponentCommands = Scripted?.Tick(opponentFrame) ?? Opponent?.Tick(opponentFrame) ?? [];
        foreach (GameCommand command in opponentCommands) Sim.Submit(OpponentPlayer, command);
        Sim.Step();
    }

    /// <summary>Steps until the match ends or <paramref name="seconds"/> of game time have passed; <paramref name="afterFrame"/> runs after every frame.</summary>
    public void RunUntil(double seconds, Action? afterFrame = null)
    {
        long end = GameTime.FromSeconds(seconds).Frame;
        while (!Sim.MatchEnded && Sim.Time.Frame < end)
        {
            Frame();
            afterFrame?.Invoke();
        }
    }

    public IReadOnlyList<ObservedEntity> OwnEntities(PlayerId player) =>
        Sim.Observe(player, ObservationMode.Oracle).Entities.Where(e => e.Owner == player).ToList();

    public void Dispose()
    {
        Arm.Dispose();
        Opponent?.Dispose();
    }
}
