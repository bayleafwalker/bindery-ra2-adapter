// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;
using Xunit;
using static Bindery.Ra2.Adapter.Tests.AssemblerTestKit;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>
/// What a frame says it knows: v1 telemetry reports no queues, and credits and power only once sampled, so the
/// frame says so instead of presenting empty queues as idle and zeros as facts. A kill's owner is its killer, and a
/// match setup that names allies or neutral houses also names the enemies.
/// </summary>
public sealed class AssemblerKnowledgeTests
{
    [Fact]
    public void Queues_are_never_known_and_credits_and_power_only_after_their_first_sample()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        assembler.Ingest(Event("game.unit.created", Own(1, 1, 10, 10)));

        ObservationFrame before = Assert.Single(assembler.Ingest(Event("game.unit.created", Own(15, 2, 11, 10))));
        Assert.False(before.QueuesKnown);
        Assert.False(before.CreditsKnown);
        Assert.False(before.PowerKnown);

        assembler.Ingest(Event("game.economy.credits", Credits(16)));
        assembler.Ingest(Event("game.economy.power", new { frame = 16, owner = 0, produced = 200, drained = 100 }));
        ObservationFrame after = Assert.Single(assembler.Ingest(Event("game.unit.created", Own(30, 3, 12, 10))));
        Assert.False(after.QueuesKnown);
        Assert.True(after.CreditsKnown);
        Assert.True(after.PowerKnown);
        Assert.Equal(1000, after.Credits);
    }

    [Fact]
    public void A_kill_is_owned_by_its_killer_and_a_destruction_by_the_victims_owner()
    {
        Ra2ObservationAssembler assembler = NewAssembler();
        assembler.Ingest(Event("game.unit.created", Own(1, 1, 10, 10)));
        assembler.Ingest(Event("game.unit.created", Enemy(1, 9, 11, 11)));

        List<GameEvent> events = [.. assembler.Ingest(Event("game.unit.killed", new { frame = 15, id = 9, killer = 0 })).SelectMany(static f => f.Events)];

        GameEvent kill = Assert.Single(events, static e => e.Kind == GameEventKind.EntityKilledByUs);
        Assert.Equal(Self, kill.Owner);
        Assert.Equal(new EntityId(9), kill.Entity);
    }

    [Fact]
    public void A_match_setup_with_non_hostile_owners_names_the_enemies()
    {
        Ra2ObservationAssembler withSetup = new(Self, Faction.Allied, Map, frameCadence: 15, nonHostileOwners: [new PlayerId(2)]);
        withSetup.Ingest(Event("game.unit.created", Enemy(1, 20, 10, 11, owner: 2)));
        withSetup.Ingest(Event("game.unit.created", Enemy(1, 21, 10, 12, owner: 1)));
        ObservationFrame frame = Assert.Single(withSetup.Ingest(Event("game.unit.created", Own(15, 1, 10, 10))));
        Assert.Equal([new PlayerId(1)], frame.Enemies!);

        Ra2ObservationAssembler without = NewAssembler();
        without.Ingest(Event("game.unit.created", Enemy(1, 21, 10, 12, owner: 1)));
        Assert.Null(Assert.Single(without.Ingest(Event("game.unit.created", Own(15, 1, 10, 10)))).Enemies);
    }
}
