// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot.Belief;
using Xunit;

namespace Bindery.Ra2.Bot.Tests.Belief;

public sealed class BeliefModelTests
{
    private static readonly PlayerId Self = new(0);
    private static readonly PlayerId EnemyPlayer = new(1);

    private static readonly UnitRule Rifleman = FakeRulesDatabase.Rule("rifleman", Faction.Allied, EntityKind.Infantry, UnitRole.AntiInfantry, QueueKind.Infantry, 100);
    private static readonly UnitRule Harvester = FakeRulesDatabase.Rule("harvester", Faction.Allied, EntityKind.Vehicle, UnitRole.Harvester, QueueKind.Vehicle, 1400);

    private static BeliefModel NewModel(double confidenceHalfLife = 60.0, double vacancyHalfLife = 10.0, double floor = 0.05) =>
        new(new FakeRulesDatabase([Rifleman, Harvester]), new BeliefOptions(confidenceHalfLife, vacancyHalfLife, floor));

    private static ObservationFrame Frame(
        double seconds,
        IReadOnlyList<ObservedEntity> entities,
        IReadOnlySet<RegionId>? visible = null,
        IReadOnlyList<GameEvent>? events = null) =>
        new(
            GameTime.FromSeconds(seconds), ObservationMode.Belief, Self, Faction.Allied, 5000,
            new PowerState(100, 50), entities, [], events ?? [], visible ?? new HashSet<RegionId>(),
            TestMaps.Simple());

    [Fact]
    public void UnknownOwnType_IsCountedAsSupportWithZeroValue()
    {
        BeliefModel model = NewModel();
        ObservedEntity mystery = new(new EntityId(1), Self, "unknown-type", new Cell(10, 10), 100, 100);

        BeliefSnapshot snapshot = model.Apply(Frame(0, [mystery]));

        OwnEntity entity = Assert.Single(snapshot.Own);
        Assert.Equal(UnitRole.Support, entity.Role);
        Assert.Equal(0, entity.Value);
    }

    [Fact]
    public void EnemySighting_SetsFullConfidence()
    {
        BeliefModel model = NewModel();
        ObservedEntity enemy = new(new EntityId(2), EnemyPlayer, "rifleman", new Cell(90, 90), 100, 100);

        BeliefSnapshot snapshot = model.Apply(Frame(0, [enemy], visible: new HashSet<RegionId> { TestMaps.EnemyStart }));

        EnemyContact contact = Assert.Single(snapshot.Enemies);
        Assert.Equal(1.0, contact.Confidence);
        Assert.False(contact.ConfirmedDestroyed);
        Assert.Equal(100, contact.Value);
    }

    /// <summary>
    /// Forces the failure case for the "never refreshes without a sighting"
    /// invariant: if the implementation mistakenly reset the decay clock on
    /// every Apply (rather than only on an actual sighting), this would stay
    /// at confidence 1.0 forever. It must decay purely from elapsed time
    /// since the *one* real sighting.
    /// </summary>
    [Fact]
    public void Confidence_DecaysWithElapsedTimeSinceLastSighting_NeverRefreshesWithoutASighting()
    {
        BeliefModel model = NewModel(confidenceHalfLife: 60.0);
        ObservedEntity enemy = new(new EntityId(2), EnemyPlayer, "rifleman", new Cell(90, 90), 100, 100);

        // Seen once, then the region goes dark (not in VisibleRegions) for many frames.
        model.Apply(Frame(0, [enemy], visible: new HashSet<RegionId> { TestMaps.EnemyStart }));
        BeliefSnapshot mid = model.Apply(Frame(30, []));
        BeliefSnapshot later = model.Apply(Frame(60, []));

        EnemyContact midContact = Assert.Single(mid.Enemies);
        EnemyContact laterContact = Assert.Single(later.Enemies);

        Assert.Equal(Math.Pow(0.5, 30.0 / 60.0), midContact.Confidence, precision: 6);
        Assert.Equal(0.5, laterContact.Confidence, precision: 6);
        Assert.True(laterContact.Confidence < midContact.Confidence);
    }

    /// <summary>
    /// Forces the failure case for "reduce confidence, not destroyed, unless
    /// the event said destroyed": a unit missing from a region we can
    /// currently see must NOT be marked ConfirmedDestroyed just from that
    /// absence.
    /// </summary>
    [Fact]
    public void AbsentFromVisibleRegion_ReducesConfidence_ButDoesNotConfirmDestroyed()
    {
        BeliefModel model = NewModel(vacancyHalfLife: 10.0, floor: 0.01);
        ObservedEntity enemy = new(new EntityId(3), EnemyPlayer, "rifleman", new Cell(90, 90), 100, 100);

        model.Apply(Frame(0, [enemy], visible: new HashSet<RegionId> { TestMaps.EnemyStart }));
        // Now we can see the region again, but the unit is gone, with no destroyed event.
        BeliefSnapshot after = model.Apply(Frame(10, [], visible: new HashSet<RegionId> { TestMaps.EnemyStart }));

        EnemyContact contact = Assert.Single(after.Enemies);
        Assert.False(contact.ConfirmedDestroyed);
        // Vacancy evidence: 10 s against the 10 s vacancy half-life, not the 60 s ordinary half-life.
        Assert.Equal(0.5, contact.Confidence, precision: 6);
    }

    /// <summary>
    /// Contrast case for vacancy decay: the same 10 s absence with the
    /// last-seen region out of sight is no evidence the unit left, so only
    /// the slower ordinary half-life applies.
    /// </summary>
    [Fact]
    public void AbsentFromUnseenRegion_DecaysOnTheOrdinaryHalfLife_NotTheVacancyHalfLife()
    {
        BeliefModel model = NewModel(confidenceHalfLife: 60.0, vacancyHalfLife: 10.0, floor: 0.01);
        ObservedEntity enemy = new(new EntityId(3), EnemyPlayer, "rifleman", new Cell(90, 90), 100, 100);

        model.Apply(Frame(0, [enemy], visible: new HashSet<RegionId> { TestMaps.EnemyStart }));
        BeliefSnapshot after = model.Apply(Frame(10, [], visible: new HashSet<RegionId> { TestMaps.Home }));

        EnemyContact contact = Assert.Single(after.Enemies);
        Assert.False(contact.ConfirmedDestroyed);
        Assert.Equal(Math.Pow(0.5, 10.0 / 60.0), contact.Confidence, precision: 6);
    }

    [Fact]
    public void ExplicitDestroyedEvent_ConfirmsDestructionAndStopsDecay()
    {
        BeliefModel model = NewModel();
        ObservedEntity enemy = new(new EntityId(4), EnemyPlayer, "rifleman", new Cell(90, 90), 100, 100);
        model.Apply(Frame(0, [enemy], visible: new HashSet<RegionId> { TestMaps.EnemyStart }));

        GameEvent destroyed = new(GameEventKind.EntityDestroyed, GameTime.FromSeconds(1), new EntityId(4), EnemyPlayer, "rifleman", new Cell(90, 90));
        BeliefSnapshot afterDestroyed = model.Apply(Frame(1, [], events: [destroyed]));
        BeliefSnapshot muchLater = model.Apply(Frame(500, [], events: []));

        Assert.True(Assert.Single(afterDestroyed.Enemies).ConfirmedDestroyed);
        EnemyContact stillThere = Assert.Single(muchLater.Enemies);
        Assert.True(stillThere.ConfirmedDestroyed);
        Assert.Equal(1.0, stillThere.Confidence);
    }

    [Fact]
    public void Contact_ForgottenOnceConfidenceFallsBelowFloor()
    {
        BeliefModel model = NewModel(confidenceHalfLife: 1.0, floor: 0.1);
        ObservedEntity enemy = new(new EntityId(5), EnemyPlayer, "rifleman", new Cell(90, 90), 100, 100);

        model.Apply(Frame(0, [enemy], visible: new HashSet<RegionId> { TestMaps.EnemyStart }));
        BeliefSnapshot forgotten = model.Apply(Frame(20, []));

        Assert.Empty(forgotten.Enemies);
    }

    [Fact]
    public void Version_IncrementsOnEveryApply()
    {
        BeliefModel model = NewModel();
        BeliefSnapshot first = model.Apply(Frame(0, []));
        BeliefSnapshot second = model.Apply(Frame(1, []));

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(second, model.Current);
    }

    [Fact]
    public void Current_ThrowsBeforeFirstApply()
    {
        BeliefModel model = NewModel();
        Assert.Throws<InvalidOperationException>(() => model.Current);
    }

    [Fact]
    public void SuspectedStart_SetFromEnemyBuildingAtAStartLocation()
    {
        UnitRule barracks = FakeRulesDatabase.Rule("barracks", Faction.Allied, EntityKind.Building, UnitRole.Production, QueueKind.Infantry, 500);
        BeliefModel model = new(new FakeRulesDatabase([Rifleman, Harvester, barracks]), new BeliefOptions());

        ObservedEntity building = new(new EntityId(6), EnemyPlayer, "barracks", new Cell(90, 90), 500, 500);
        BeliefSnapshot snapshot = model.Apply(new ObservationFrame(
            GameTime.FromSeconds(0), ObservationMode.Belief, Self, Faction.Allied, 5000, new PowerState(100, 50),
            [building], [], [], new HashSet<RegionId> { TestMaps.EnemyStart }, TestMaps.Simple()));

        EnemyPlayerBelief belief = Assert.Single(snapshot.EnemyPlayers);
        Assert.Equal(TestMaps.EnemyStart, belief.SuspectedStart);
        Assert.Contains("barracks", belief.SeenTech);
    }
}
