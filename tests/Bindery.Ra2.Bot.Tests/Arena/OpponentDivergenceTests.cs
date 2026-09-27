// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Arena;
using Bindery.Ra2.Bot.Playbooks;
using Bindery.Ra2.Bot.Rules;
using Bindery.Ra2.Bot.Sim;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Arena;

/// <summary>
/// The training scripted styles must be different opponents in practice, not only in name: against the selector
/// on the standard benchmark, ai-rush, ai-balanced and ai-turtle must lead to different games (different arm
/// decision logs) on most map × seed cells. Styles that share an opening and act only after the bot has already
/// won are one opponent counted three times.
/// </summary>
public sealed class OpponentDivergenceTests
{
    [Fact]
    public void Training_scripted_styles_play_different_games_against_the_selector()
    {
        IRulesDatabase rules = RulesDatabase.LoadEmbeddedFixture();
        BotAgentFactory factory = new(rules, PlaybookLibrary.LoadDefault(), new ArenaRunContext(llmFake: true, null));
        string[] styles = ["ai-rush", "ai-balanced", "ai-turtle"];
        List<(SimMap Map, int Seed)> cells = [.. SimMaps.Training.SelectMany(static m => new[] { (m, 1), (m, 2) })];

        Dictionary<string, List<string?>> logs = styles.ToDictionary(static s => s, _ => new List<string?>());
        foreach (string style in styles)
        {
            foreach ((SimMap map, int seed) in cells)
            {
                logs[style].Add(MatchRunner.Run(new ArmSpec("selector", false, true), style, map, "training", seed, 600, rules, factory).Players["arm"].DecisionLogHash);
            }
        }

        foreach ((string a, string b) in new[] { ("ai-rush", "ai-balanced"), ("ai-rush", "ai-turtle"), ("ai-balanced", "ai-turtle") })
        {
            int same = logs[a].Zip(logs[b]).Count(static p => p.First == p.Second);
            Assert.True(same * 3 <= cells.Count, $"{a} and {b} gave the arm identical games on {same} of {cells.Count} cells");
        }
    }
}
