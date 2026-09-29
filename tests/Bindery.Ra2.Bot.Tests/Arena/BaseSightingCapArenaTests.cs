// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>An arena <c>--knob BaseSightingWeightCap</c> reaches the planner's options end to end.</summary>
public sealed partial class BaseSightingCapArenaTests
{
    [GeneratedRegex(@"w (\d\.\d\d), upper \d+, baseAge (\d+)s")]
    private static partial Regex GateNote();

    /// <summary>Weights from every gate note written while a base had been seen (finite base age).</summary>
    private static List<double> BaseSeenWeights(double? cap)
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        ArenaRunContext context = new(llmFake: true, null)
        {
            ArmKnobs = cap is { } c ? new Dictionary<string, double> { ["BaseSightingWeightCap"] = c } : new Dictionary<string, double>(),
        };
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), context);
        SimMap map = SimMaps.HeldOut.Single(static m => m.Map.MapId == "fortress-choke");
        List<DecisionRecord> log = [];
        MatchRunner.Run(new ArmSpec("selector", false, true), "ai-horde:hard", map, "heldout", 2, 400, rules, factory, armLog: r => log.AddRange(r), benchmark: BenchmarkSettings.Contested);
        return [.. log.Where(static r => r.Data.TryGetProperty("notes", out _))
            .SelectMany(static r => r.Data.GetProperty("notes").EnumerateArray().Select(static n => n.GetString() ?? ""))
            .Select(static n => GateNote().Match(n))
            .Where(static m => m.Success && int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) < 999)
            .Select(static m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];
    }

    [Fact]
    public void Capped_arm_completes_and_notes_show_the_capped_weight()
    {
        List<double> capped = BaseSeenWeights(0.5);
        Assert.NotEmpty(capped);
        Assert.Contains(capped, static w => w <= 0.5);
        // Without the knob a fresh base sighting gives full weight, so the cap is what changed it.
        Assert.Contains(BaseSeenWeights(null), static w => w > 0.5);
    }
}
