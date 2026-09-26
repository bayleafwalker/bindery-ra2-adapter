// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Bot.Arbitration;
using Bindery.Ra2.Bot.Claude;
using Bindery.Ra2.Bot.Runtime;
using Bindery.Ra2.Bot.Strategy;

namespace Bindery.Ra2.Bot.Arena;

/// <summary>
/// An arena side driven by a full <see cref="BotRuntime"/>. Every metric it reports
/// is read back from the runtime's metrics and decision log after the match, so the
/// arena scores exactly what the log records.
/// </summary>
public sealed class BotArenaAgent : IArenaAgent
{
    private readonly BotRuntime runtime;
    private readonly DecisionLog log;
    private readonly Action<bool?, double, double, BotArenaAgent>? onFinish;

    public BotArenaAgent(BotRuntime runtime, DecisionLog log, IEnumerable<string> labels, Action<bool?, double, double, BotArenaAgent>? onFinish = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(log);
        this.runtime = runtime;
        this.log = log;
        this.onFinish = onFinish;
        Stats.Labels.AddRange(labels);
    }

    public BotRuntime Runtime => runtime;

    public ArenaAgentStats Stats { get; } = new();

    public IReadOnlyList<DecisionRecord> DecisionLog => log.Records;

    /// <summary>Claude strategists in this agent, for cost accounting.</summary>
    public List<ClaudeStrategist> ClaudeStrategists { get; } = [];

    public IReadOnlyList<GameCommand> Tick(ObservationFrame frame)
    {
        IReadOnlyList<GameCommand> commands = runtime.Tick(frame);
        foreach (DroppedCommand dropped in runtime.LastDropped)
        {
            Stats.DroppedByReason[dropped.Reason] = Stats.DroppedByReason.GetValueOrDefault(dropped.Reason) + 1;
        }
        return commands;
    }

    public void Finish(bool? won, double ownAssetValue, double enemyAssetValue)
    {
        // The result goes into the log itself, so a post-game report (and a replay) sees how the match ended.
        if (runtime.CurrentFeatures is { } last)
        {
            log.Write(new DecisionRecord(DecisionRecordKinds.MatchResult, last.Time, last.SnapshotVersion, BotJson.ToElement(new
            {
                result = won switch { true => "won", false => "lost", null => "draw" },
                ownAssetValue,
                enemyAssetValue,
            })));
        }
        BotMetrics m = runtime.Metrics;
        Stats.Proposals = (int)m.Proposals;
        Stats.Rejected = (int)m.Rejected;
        Stats.LateDiscarded = (int)m.LateDiscarded;
        Stats.Activations = (int)m.Activations;
        Stats.PostureFlips = (int)m.PostureFlips;
        Stats.CommandsDropped = (int)m.CommandsDropped;
        Stats.ShadowProposals = (int)m.ShadowProposals;
        Stats.ProposalsFailed = (int)m.ProposalsFailed;

        foreach (DecisionRecord record in log.Records)
        {
            if (record.Kind == DecisionRecordKinds.Proposal && record.Data.TryGetProperty("latencyFrames", out JsonElement frames))
            {
                Stats.LateSeconds.Add(frames.GetInt64() / (double)GameTime.FramesPerSecond);
            }
            if (record.Kind == DecisionRecordKinds.Validation && record.Data.TryGetProperty("issues", out JsonElement issues))
            {
                foreach (JsonElement issue in issues.EnumerateArray())
                {
                    if (issue.TryGetProperty("code", out JsonElement code) && code.GetString() is { } c
                        && c.StartsWith("fog.", StringComparison.Ordinal)
                        && issue.TryGetProperty("severity", out JsonElement severity) && severity.GetString() == nameof(ValidationSeverity.Reject))
                    {
                        Stats.FogRejections++;
                    }
                }
            }
            if ((record.Kind == DecisionRecordKinds.Proposal || record.Kind == DecisionRecordKinds.ShadowProposal)
                && record.Data.TryGetProperty("cost", out JsonElement cost) && cost.ValueKind == JsonValueKind.Object)
            {
                long input = cost.TryGetProperty("inputTokens", out JsonElement i) ? i.GetInt64() : 0;
                long output = cost.TryGetProperty("outputTokens", out JsonElement o) ? o.GetInt64() : 0;
                long cached = cost.TryGetProperty("cacheReadTokens", out JsonElement c) ? c.GetInt64() : 0;
                string? model = cost.TryGetProperty("model", out JsonElement md) && md.ValueKind == JsonValueKind.String ? md.GetString() : null;
                Stats.TokensIn += input + cached;
                Stats.TokensOut += output;
                if (model is not null)
                {
                    Stats.Model ??= model;
                    Stats.Usd += PriceTable.CostUsd(model, input, output, cached) ?? 0;
                }
            }
        }
        CountDistillation();
        Analysis.PostGameReport analysis = Analysis.PostGameReport.Build(log.Records);
        Stats.ShadowCompared = analysis.Shadow.Compared;
        Stats.ShadowAgreed = analysis.Shadow.Agreed;
        foreach ((string playbook, double seconds) in analysis.PlaybookSeconds) Stats.PlaybookSeconds[playbook] = seconds;
        foreach ((string posture, double seconds) in analysis.PostureSeconds) Stats.PostureSeconds[posture] = seconds;
        // Failed requests still cost tokens; the Claude strategist reports them through LastFailure only.
        Stats.DecisionLogHash = log.ComputeHash();
        onFinish?.Invoke(won, ownAssetValue, enemyAssetValue, this);
    }

    /// <summary>
    /// Distilled decisions and escalations, from the log: every primary request to the distilled strategist is a
    /// decision, and every one its own model did not answer is an escalation. Its own answers are the proposals of
    /// source Distilled (the model itself always answers, at once); everything else was escalated, whether the inner
    /// strategist answered, failed, or was still waiting when the match ended (the scheduler cancels that request
    /// without a record, so counting only answers and failures would undercount escalations).
    /// </summary>
    private void CountDistillation()
    {
        int requests = 0, ownAnswers = 0;
        foreach (DecisionRecord record in log.Records)
        {
            if (record.Data.ValueKind != JsonValueKind.Object
                || !record.Data.TryGetProperty("strategistId", out JsonElement id) || id.GetString() != DistilledId
                || !record.Data.TryGetProperty("role", out JsonElement role) || role.GetString() != "Primary")
            {
                continue;
            }
            if (record.Kind == RuntimeRecordKinds.Request) requests++;
            else if (record.Kind == DecisionRecordKinds.Proposal
                && record.Data.GetProperty("intent").GetProperty("source").GetString() == nameof(IntentSource.Distilled))
            {
                ownAnswers++;
            }
        }
        Stats.DistilledDecisions += requests;
        Stats.DistilledEscalations += requests - ownAnswers;
    }

    /// <summary>The distilled strategist's id in decision logs.</summary>
    public const string DistilledId = "distilled";

    public void Dispose() => runtime.Dispose();
}
