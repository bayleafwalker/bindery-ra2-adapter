// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Playbooks;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Claude;

public sealed class PhasedCatalogueTests
{
    private static JsonDocument MatchContext(IPlaybookLibrary library)
    {
        StrategistContext context = ClaudeFixtures.Context() with { Playbooks = library };
        return JsonDocument.Parse(new IntentPromptBuilder().Build(context, StrategistMode.Strategic).MatchContext);
    }

    [Fact]
    public void A_phased_playbook_lists_its_phases_with_enter_conditions_and_overrides()
    {
        using JsonDocument match = MatchContext(new PlaybookLibrary([Arbitration.PhaseTests.ExpandTechAttack()]));
        JsonElement phases = match.RootElement.GetProperty("catalogue")[0].GetProperty("phases");

        Assert.Equal(["expand", "tech", "attack"], phases.EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToList());
        Assert.Equal(0, phases[0].GetProperty("enterWhen").GetArrayLength());
        Assert.False(phases[0].TryGetProperty("budget", out _));
        Assert.Equal("Tech", phases[1].GetProperty("posture").GetString());
        Assert.Equal(0.5, phases[1].GetProperty("budget").GetProperty("tech").GetDouble());
        Assert.False(phases[1].TryGetProperty("composition", out _));
        Assert.Equal(1, phases[2].GetProperty("enterWhen").GetArrayLength());
        Assert.Equal(1, phases[2].GetProperty("attackConditions").GetArrayLength());
    }

    [Fact]
    public void A_playbook_without_phases_has_no_phases_key()
    {
        using JsonDocument match = MatchContext(new FakePlaybooks());
        Assert.All(match.RootElement.GetProperty("catalogue").EnumerateArray(), p => Assert.False(p.TryGetProperty("phases", out _)));
    }
}
