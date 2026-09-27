// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot;

namespace Bindery.Ra2.Adapter.Bot;

/// <summary>
/// Loads a <see cref="MapInfo"/> from operator-authored JSON (an RA2 map's
/// regions, links and ore fields), serialised with <see cref="BotJson.Options"/>
/// so the file round-trips with anything the bot itself writes. Map info is
/// static, shroud-independent knowledge — never derived from telemetry — so
/// it is always supplied by the caller, not the assembler.
/// </summary>
public static class MapInfoLoader
{
    /// <summary>Parses a <see cref="MapInfo"/> from a JSON string.</summary>
    public static MapInfo Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return Deserialize(json);
    }

    /// <summary>Parses a <see cref="MapInfo"/> from a UTF-8 JSON stream.</summary>
    public static MapInfo Load(Stream utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        MapInfo? map = JsonSerializer.Deserialize<MapInfo>(utf8Json, BotJson.Options);
        return map ?? throw new InvalidDataException("map info JSON deserialized to null");
    }

    /// <summary>Reads and parses a <see cref="MapInfo"/> JSON file from disk.</summary>
    public static MapInfo LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return Load(stream);
    }

    private static MapInfo Deserialize(string json)
    {
        MapInfo? map = JsonSerializer.Deserialize<MapInfo>(json, BotJson.Options);
        return map ?? throw new InvalidDataException("map info JSON deserialized to null");
    }
}
