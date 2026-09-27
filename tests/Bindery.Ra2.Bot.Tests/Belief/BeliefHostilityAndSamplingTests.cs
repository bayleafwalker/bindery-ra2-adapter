// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Belief;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Belief;

/// <summary>
/// What a frame says about who is an enemy (<see cref="ObservationFrame.Enemies"/>) and whether credits and power
/// were sampled at all (<see cref="ObservationFrame.CreditsKnown"/>, <see cref="ObservationFrame.PowerKnown"/>).
/// </summary>
public sealed class BeliefHostilityAndSamplingTests
{
    private static readonly PlayerId Self = new(0);
    private static readonly PlayerId Enemy = new(1);
    private static readonly PlayerId Neutral = new(7);

    private static readonly UnitRule Tank = FakeRulesDatabase.Rule("tank", Faction.Allied, EntityKind.Vehicle, UnitRole.AntiArmor, QueueKind.Vehicle, 900);
    private static readonly UnitRule Hut = FakeRulesDatabase.Rule("hut", Faction.Allied, EntityKind.Building, UnitRole.Support, QueueKind.Building, 100);

    private static ObservationFrame Frame(double seconds, IReadOnlyList<ObservedEntity> entities, IReadOnlySet<PlayerId>? enemies = null,
        int credits = 5000, bool creditsKnown = true, bool powerKnown = true) =>
        new(GameTime.FromSeconds(seconds), ObservationMode.Belief, Self, Faction.Allied, credits, new PowerState(100, 50), entities, [], [],
            new HashSet<RegionId> { TestMaps.Home, TestMaps.Middle }, TestMaps.Simple(),
            CreditsKnown: creditsKnown, PowerKnown: powerKnown, Enemies: enemies);

    [Fact]
    public void Owners_the_frame_does_not_name_as_enemies_are_neither_contacts_nor_enemy_players()
    {
        BeliefModel model = new(new FakeRulesDatabase([Tank, Hut]), new BeliefOptions());
        ObservedEntity enemyTank = new(new EntityId(40), Enemy, "tank", new Cell(30, 30), 100, 100);
        ObservedEntity civilianHut = new(new EntityId(41), Neutral, "hut", new Cell(32, 30), 100, 100);

        BeliefSnapshot snapshot = model.Apply(Frame(0, [enemyTank, civilianHut], enemies: new HashSet<PlayerId> { Enemy }));

        Assert.Equal([enemyTank.Id], snapshot.Enemies.Select(static c => c.Id));
        Assert.Equal([Enemy], snapshot.EnemyPlayers.Select(static p => p.Player));
    }

    [Fact]
    public void Without_an_enemies_set_every_other_owner_is_an_enemy()
    {
        BeliefModel model = new(new FakeRulesDatabase([Tank, Hut]), new BeliefOptions());
        ObservedEntity civilianHut = new(new EntityId(41), Neutral, "hut", new Cell(32, 30), 100, 100);

        BeliefSnapshot snapshot = model.Apply(Frame(0, [civilianHut]));

        Assert.Single(snapshot.Enemies);
    }

    [Fact]
    public void Unsampled_credits_and_power_are_flagged_and_later_gaps_keep_the_last_sample()
    {
        BeliefModel model = new(new FakeRulesDatabase([Tank]), new BeliefOptions());

        BeliefSnapshot before = model.Apply(Frame(0, [], credits: 0, creditsKnown: false, powerKnown: false));
        Assert.False(before.CreditsKnown);
        Assert.False(before.PowerKnown);

        BeliefSnapshot sampled = model.Apply(Frame(1, [], credits: 3000));
        Assert.True(sampled.CreditsKnown);
        Assert.Equal(3000, sampled.Credits);

        BeliefSnapshot gap = model.Apply(Frame(2, [], credits: 0, creditsKnown: false, powerKnown: false));
        Assert.True(gap.CreditsKnown);
        Assert.True(gap.PowerKnown);
        Assert.Equal(3000, gap.Credits);
        Assert.Equal(new PowerState(100, 50), gap.Power);
    }
}
