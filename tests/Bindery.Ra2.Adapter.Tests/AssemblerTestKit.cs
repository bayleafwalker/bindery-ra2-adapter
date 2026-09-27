// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter.Bot;
using Bindery.Ra2.Bot;

namespace Bindery.Ra2.Adapter.Tests;

/// <summary>Shared telemetry builders for the assembler tests: a two-region map with our base far from the enemy's.</summary>
internal static class AssemblerTestKit
{
    public static readonly PlayerId Self = new(0);

    // Region 1 is our base; region 2 is far away (the enemy's side of the map).
    public static readonly MapInfo Map = new(
        "fog-2",
        128,
        128,
        [
            new Region(new RegionId(1), "Base", new Cell(10, 10), 8, true, true, false),
            new Region(new RegionId(2), "Far", new Cell(100, 100), 8, true, true, false),
        ],
        [],
        []);

    private static int sequence;

    public static NormalizedObservation Event(string eventType, object payload)
    {
        string id = $"evt-{Interlocked.Increment(ref sequence)}";
        return new(
            $"derived-{id}",
            eventType,
            JsonSerializer.SerializeToElement(payload, BotJson.Options),
            [id],
            [new SourceRange("capture-1", 1, 1, "hash")],
            "1.0.0",
            "adapter-test",
            "bindery.ra2.normalizer",
            "0.1.0");
    }

    public static Ra2ObservationAssembler NewAssembler() => new(Self, Faction.Allied, Map, frameCadence: 15);

    public static object Enemy(long frame, long id, int x, int y, bool visible = true, int owner = 1, int health = 100) =>
        new { frame, owner, id, type = "HTNK", x, y, health, maxHealth = 100, visible };

    public static object Own(long frame, long id, int x, int y, long health = 400) =>
        new { frame, owner = 0, id, type = "MTNK", x, y, health, maxHealth = 400 };

    public static object Credits(long frame) => new { frame, owner = 0, credits = 1000 };
}
