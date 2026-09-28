// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bindery.Ra2.Adapter.Channel;

/// <summary>One cell of the comparison: a map, and who was in the agent seat.</summary>
public sealed record ExperimentRow(
    string MapId,
    string Controller,
    int Matches,
    int Completed,
    int AgentWins,
    int AgentLosses,
    int Undecided,
    double MeanPlaybookRevisions,
    IReadOnlyList<int> Seeds,
    int TracesInBindery,
    // Matches whose first attempt was classified a startup crash and retried;
    // the retry's own outcome is counted normally above.
    int StartupCrashesRetried);

/// <summary>
/// Compares controllers across channel matches -- the point of recording
/// map, seed, controller and outcome for every match.
/// </summary>
/// <remarks>
/// Only <see cref="ChannelMatchOutcome.Completed"/> matches count toward
/// wins and losses; an incomplete match proves nothing about the controller.
/// A completed match whose winner the telemetry could not name is
/// <c>Undecided</c>, never a loss. Rows are ordered by map, then controller,
/// so the same records always produce the same report.
/// </remarks>
public static class ChannelExperimentReport
{
    public const string NoAgent = "(no agent)";

    private static readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Reads records written by <see cref="NdjsonChannelRecordSink"/>.</summary>
    public static IReadOnlyList<ChannelMatchRecord> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        List<ChannelMatchRecord> records = [];
        int line = 0;
        foreach (string text in File.ReadLines(path))
        {
            line++;
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                records.Add(JsonSerializer.Deserialize<ChannelMatchRecord>(text, options) ?? throw new JsonException("empty record"));
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"{path}:{line}: {exception.Message}", exception);
            }
        }
        return records;
    }

    public static IReadOnlyList<ExperimentRow> Build(IEnumerable<ChannelMatchRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .GroupBy(static r => (r.MapId, Controller: ControllerName(r)))
            .OrderBy(static g => g.Key.MapId, StringComparer.Ordinal)
            .ThenBy(static g => g.Key.Controller, StringComparer.Ordinal)
            .Select(static group =>
            {
                ChannelMatchRecord[] completed = group.Where(static r => r.Outcome == ChannelMatchOutcome.Completed).ToArray();
                int wins = completed.Count(static r => r.AgentHouse is not null && r.Winner is not null && string.Equals(r.Winner, r.AgentHouse, StringComparison.Ordinal));
                int losses = completed.Count(static r => r.AgentHouse is not null && r.Winner is not null && !string.Equals(r.Winner, r.AgentHouse, StringComparison.Ordinal));
                int undecided = completed.Count(static r => r.Winner is null);
                return new ExperimentRow(
                    group.Key.MapId,
                    group.Key.Controller,
                    // Matches, not attempts: a retried match has two records.
                    group.Count(static r => r.Attempt == 1),
                    completed.Length,
                    wins,
                    losses,
                    undecided,
                    completed.Length == 0 ? 0 : Math.Round(completed.Average(static r => r.PlaybookRevisions), 2),
                    group.Select(static r => r.Seed).OfType<int>().Distinct().Order().ToArray(),
                    group.Count(static r => r.DecisionTraceContentHash is not null),
                    group.Count(static r => r.FailureClass == ChannelRunner.StartupCrashFailureClass && r.Attempt == 1));
            })
            .ToArray();
    }

    public static string RenderTable(IReadOnlyList<ExperimentRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        StringBuilder text = new();
        text.Append("map\tcontroller\tmatches\tcompleted\twins\tlosses\tundecided\tmean_revisions\tseeds\ttraces_in_bindery\tstartup_crashes_retried\n");
        foreach (ExperimentRow row in rows)
        {
            text.Append(row.MapId).Append('\t')
                .Append(row.Controller).Append('\t')
                .Append(row.Matches.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.Completed.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.AgentWins.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.AgentLosses.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.Undecided.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.MeanPlaybookRevisions.ToString("0.##", CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.Seeds.Count.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.TracesInBindery.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(row.StartupCrashesRetried.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return text.ToString();
    }

    private static string ControllerName(ChannelMatchRecord record) =>
        record.AgentController is { } controller
            ? controller.ControllerId is null ? controller.Kind : $"{controller.ControllerId}@{controller.ControllerVersion}"
            : NoAgent;
}
