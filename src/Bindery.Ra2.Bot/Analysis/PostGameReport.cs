// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Bot.Runtime;

namespace Bindery.Ra2.Bot.Analysis;

/// <summary>One intent that took effect: when, what, who proposed it, why, and how it ended.</summary>
/// <param name="Trigger">What started the request that produced it (<c>initial</c>, <c>cadence</c>, <c>event:BaseUnderAttack</c>, <c>replan:…</c>); null for an emergency intent.</param>
/// <param name="ActivationReason">The arbiter's reason (<c>no_active</c>, <c>switch</c>, <c>override:abort</c>, …).</param>
/// <param name="Renewals">Proposals that renewed it (same playbook and posture) while it was active.</param>
public sealed record TimelineEntry(
    double AtSeconds,
    string IntentId,
    string PlaybookId,
    string Posture,
    string Source,
    string Role,
    double Confidence,
    string? Rationale,
    string? Trigger,
    string ActivationReason,
    int Renewals,
    double? EndedAtSeconds,
    string? EndReason);

/// <summary>A change of playbook or posture and what led to it.</summary>
/// <param name="PreviousEndReason">Why the previous intent ended (<c>expired</c>, <c>replaced:switch</c>, <c>replaced:override:abort</c>, …).</param>
public sealed record Pivot(double AtSeconds, string FromPlaybook, string FromPosture, string ToPlaybook, string ToPosture, string? Trigger, string ActivationReason, string? PreviousEndReason, string? Rationale);

/// <summary>A proposal that did not take effect.</summary>
/// <param name="Outcome"><c>rejected</c> (validator), <c>late</c> (freshness) or <c>refused</c> (arbiter: commitment, hysteresis, …).</param>
/// <param name="Codes">Validator reject codes, the late reason, or the arbiter reason.</param>
public sealed record RejectedProposal(double AtSeconds, string Role, string StrategistId, string? IntentId, string? PlaybookId, string Outcome, IReadOnlyList<string> Codes);

/// <summary>A moment worth knowing about, from triggers, intent ends and operational notes.</summary>
public sealed record KeyEvent(double AtSeconds, string Kind, string Detail);

/// <summary>What the shadow strategist would have done, against what the active strategist did for the same request.</summary>
/// <param name="Compared">Shadow answers whose request also got a primary proposal.</param>
/// <param name="Agreed">Of those, answers naming the same playbook as the primary proposal.</param>
/// <param name="PostureAgreed">Of those, answers naming the same posture.</param>
/// <param name="WouldHaveActivated">Shadow answers the arbiter would have activated or renewed.</param>
public sealed record ShadowAgreement(int Proposals, int Compared, int Agreed, int PostureAgreed, int WouldHaveActivated, IReadOnlyDictionary<string, int> ShadowPlaybooks)
{
    public double? AgreementRate => Compared == 0 ? null : Agreed / (double)Compared;
}

/// <summary>
/// A deterministic post-game analysis built from a decision log alone: a timeline of the intents that took effect
/// with their rationale and trigger, the pivots and what caused them, the proposals that did not take effect and
/// why, key events, and how often a shadow strategist agreed with the active one. It reads only records the runtime
/// writes (see <see cref="DecisionRecordKinds"/> and <see cref="RuntimeRecordKinds"/>), so it works on a live run's
/// log, an arena match log and a replay alike, and the same log always gives the same report.
/// </summary>
public sealed record PostGameReport(
    double DurationSeconds,
    string? Result,
    int Requests,
    int Proposals,
    int Activations,
    int Renewals,
    int PostureFlips,
    IReadOnlyList<TimelineEntry> Timeline,
    IReadOnlyList<Pivot> Pivots,
    IReadOnlyList<RejectedProposal> Rejected,
    IReadOnlyList<KeyEvent> KeyEvents,
    ShadowAgreement Shadow,
    IReadOnlyDictionary<string, double> PlaybookSeconds,
    IReadOnlyDictionary<string, double> PostureSeconds)
{
    public static PostGameReport Build(IReadOnlyList<DecisionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        double end = records.Count == 0 ? 0 : records.Max(static r => r.Time.Seconds);
        Dictionary<(string Role, long Version), string> triggers = [];
        Dictionary<long, (string Playbook, string Posture)> primaryProposals = [];
        List<TimelineEntry> timeline = [];
        List<RejectedProposal> rejected = [];
        List<KeyEvent> events = [];
        int requests = 0, proposals = 0, renewals = 0, flips = 0, shadowCount = 0, wouldBe = 0;
        List<(long Version, string Playbook, string Posture)> shadows = [];
        SortedDictionary<string, int> shadowPlaybooks = new(StringComparer.Ordinal);
        string? result = null;
        string? lastEndReason = null;
        bool attacking = false, defending = false;
        string? lastEventTrigger = null;

        foreach (DecisionRecord r in records)
        {
            JsonElement d = r.Data;
            if (d.ValueKind != JsonValueKind.Object) continue;
            double t = r.Time.Seconds;
            switch (r.Kind)
            {
                case RuntimeRecordKinds.Request:
                {
                    requests++;
                    string role = Str(d, "role") ?? "?";
                    string trigger = Str(d, "trigger") ?? "?";
                    triggers[(role, r.SnapshotVersion)] = trigger;
                    if (role == "Primary" && (trigger.StartsWith("event:", StringComparison.Ordinal) || trigger.StartsWith("replan:", StringComparison.Ordinal)) && trigger != lastEventTrigger)
                    {
                        events.Add(new KeyEvent(t, "trigger", trigger));
                    }
                    if (role == "Primary") lastEventTrigger = trigger;
                    break;
                }
                case DecisionRecordKinds.Proposal:
                {
                    proposals++;
                    if (Str(d, "role") == "Primary" && d.TryGetProperty("intent", out JsonElement intent) && d.TryGetProperty("requestVersion", out JsonElement rv))
                    {
                        primaryProposals[rv.GetInt64()] = (Str(intent, "playbookId") ?? "?", Str(intent, "posture") ?? "?");
                    }
                    break;
                }
                case DecisionRecordKinds.ShadowProposal:
                {
                    shadowCount++;
                    if (d.TryGetProperty("intent", out JsonElement intent) && d.TryGetProperty("requestVersion", out JsonElement rv))
                    {
                        string playbook = Str(intent, "playbookId") ?? "?";
                        shadows.Add((rv.GetInt64(), playbook, Str(intent, "posture") ?? "?"));
                        shadowPlaybooks[playbook] = shadowPlaybooks.GetValueOrDefault(playbook) + 1;
                    }
                    if (Str(d, "wouldBe") is "Activated" or "Renewed") wouldBe++;
                    break;
                }
                case DecisionRecordKinds.Validation:
                {
                    string role = Str(d, "role") ?? "?";
                    string strategist = Str(d, "strategistId") ?? "?";
                    if (d.TryGetProperty("accepted", out JsonElement accepted) && accepted.ValueKind == JsonValueKind.False)
                    {
                        List<string> codes = [];
                        if (d.TryGetProperty("issues", out JsonElement issues) && issues.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement issue in issues.EnumerateArray())
                            {
                                if (Str(issue, "severity") == nameof(ValidationSeverity.Reject) && Str(issue, "code") is { } code) codes.Add(code);
                            }
                        }
                        rejected.Add(new RejectedProposal(t, role, strategist, Str(d, "intentId"), null, "rejected", codes));
                    }
                    else if (Str(d, "arbitration") == "Refused")
                    {
                        rejected.Add(new RejectedProposal(t, role, strategist, Str(d, "intentId"), null, "refused", [Str(d, "arbitrationReason") ?? "?"]));
                    }
                    break;
                }
                case DecisionRecordKinds.LateDiscarded:
                    rejected.Add(new RejectedProposal(t, Str(d, "role") ?? "?", Str(d, "strategistId") ?? "?", Str(d, "intentId"), null, "late", [Str(d, "reason") ?? "?"]));
                    break;
                case DecisionRecordKinds.IntentActivated:
                {
                    bool renewal = d.TryGetProperty("renewal", out JsonElement rn) && rn.ValueKind == JsonValueKind.True;
                    if (renewal)
                    {
                        renewals++;
                        if (timeline.Count > 0) timeline[^1] = timeline[^1] with { Renewals = timeline[^1].Renewals + 1 };
                        break;
                    }
                    if (d.TryGetProperty("postureFlip", out JsonElement pf) && pf.ValueKind == JsonValueKind.True) flips++;
                    JsonElement intent = d.GetProperty("intent");
                    string role = Str(d, "role") ?? "?";
                    long basedOn = intent.TryGetProperty("basedOnSnapshotVersion", out JsonElement b) ? b.GetInt64() : r.SnapshotVersion;
                    string? trigger = triggers.TryGetValue((role, basedOn), out string? tr) ? tr : null;
                    if (timeline.Count > 0 && timeline[^1].EndedAtSeconds is null) timeline[^1] = timeline[^1] with { EndedAtSeconds = t, EndReason = lastEndReason ?? "replaced" };
                    timeline.Add(new TimelineEntry(
                        t, Str(d, "intentId") ?? "?", Str(d, "playbookId") ?? "?", Str(d, "posture") ?? "?", Str(d, "source") ?? "?", role,
                        d.TryGetProperty("confidence", out JsonElement c) ? c.GetDouble() : 0,
                        Str(intent, "rationale"), trigger, Str(d, "reason") ?? "?", 0, null, null));
                    lastEndReason = null;
                    break;
                }
                case DecisionRecordKinds.PhaseChanged:
                    events.Add(new KeyEvent(t, "phase_changed", $"{Str(d, "playbookId")}: {Str(d, "fromPhase")} -> {Str(d, "toPhase")}"));
                    break;
                case DecisionRecordKinds.IntentEnded:
                {
                    string reason = Str(d, "reason") ?? "?";
                    lastEndReason = reason;
                    if (timeline.Count > 0 && timeline[^1].IntentId == Str(d, "intentId") && timeline[^1].EndedAtSeconds is null)
                    {
                        timeline[^1] = timeline[^1] with { EndedAtSeconds = t, EndReason = reason };
                    }
                    if (reason.Contains("abort", StringComparison.Ordinal) || reason.Contains("base_threat", StringComparison.Ordinal))
                    {
                        events.Add(new KeyEvent(t, "intent_ended", $"{Str(d, "playbookId")}: {reason}"));
                    }
                    break;
                }
                case DecisionRecordKinds.Plan:
                    if (d.TryGetProperty("notes", out JsonElement notes) && notes.ValueKind == JsonValueKind.Array)
                    {
                        bool attackNow = false, defendNow = false;
                        foreach (JsonElement note in notes.EnumerateArray())
                        {
                            string text = note.GetString() ?? string.Empty;
                            if (text.StartsWith("squads: attacking", StringComparison.Ordinal)) attackNow = true;
                            if (text.StartsWith("squads: defending", StringComparison.Ordinal)) defendNow = true;
                            if (text.StartsWith("superweapon:", StringComparison.Ordinal) && !text.Contains("no known", StringComparison.Ordinal)) events.Add(new KeyEvent(t, "superweapon", text));
                        }
                        if (attackNow && !attacking) events.Add(new KeyEvent(t, "attack", Note(notes, "squads: attacking")));
                        if (defendNow && !defending) events.Add(new KeyEvent(t, "defence", Note(notes, "squads: defending")));
                        attacking = attackNow;
                        defending = defendNow;
                    }
                    break;
                case DecisionRecordKinds.MatchResult:
                    result = Str(d, "result");
                    events.Add(new KeyEvent(t, "result", string.Create(CultureInfo.InvariantCulture,
                        $"{result}: own assets {(d.TryGetProperty("ownAssetValue", out JsonElement own) ? own.GetDouble() : 0):0}, enemy assets {(d.TryGetProperty("enemyAssetValue", out JsonElement enemy) ? enemy.GetDouble() : 0):0}")));
                    break;
            }
        }
        if (timeline.Count > 0 && timeline[^1].EndedAtSeconds is null) timeline[^1] = timeline[^1] with { EndReason = "match end" };

        List<Pivot> pivots = [];
        for (int i = 1; i < timeline.Count; i++)
        {
            TimelineEntry from = timeline[i - 1], to = timeline[i];
            if (from.PlaybookId == to.PlaybookId && from.Posture == to.Posture) continue;
            pivots.Add(new Pivot(to.AtSeconds, from.PlaybookId, from.Posture, to.PlaybookId, to.Posture, to.Trigger, to.ActivationReason, from.EndReason, to.Rationale));
        }

        int compared = 0, agreed = 0, postureAgreed = 0;
        foreach ((long version, string playbook, string posture) in shadows)
        {
            if (!primaryProposals.TryGetValue(version, out (string Playbook, string Posture) primary)) continue;
            compared++;
            if (primary.Playbook == playbook) agreed++;
            if (primary.Posture == posture) postureAgreed++;
        }

        SortedDictionary<string, double> playbookSeconds = new(StringComparer.Ordinal);
        SortedDictionary<string, double> postureSeconds = new(StringComparer.Ordinal);
        foreach (TimelineEntry e in timeline)
        {
            double seconds = Math.Max(0, (e.EndedAtSeconds ?? end) - e.AtSeconds);
            playbookSeconds[e.PlaybookId] = playbookSeconds.GetValueOrDefault(e.PlaybookId) + seconds;
            postureSeconds[e.Posture] = postureSeconds.GetValueOrDefault(e.Posture) + seconds;
        }

        return new PostGameReport(
            end, result, requests, proposals, timeline.Count, renewals, flips, timeline, pivots, rejected, events,
            new ShadowAgreement(shadowCount, compared, agreed, postureAgreed, wouldBe, shadowPlaybooks),
            playbookSeconds, postureSeconds);
    }

    /// <summary>A deterministic Markdown rendering.</summary>
    public string ToMarkdown(string? title = null)
    {
        StringBuilder sb = new();
        sb.AppendLine($"# {title ?? "Post-game report"}");
        sb.AppendLine();
        sb.AppendLine($"Duration {F(DurationSeconds, "0")} s; result {Result ?? "not in the log"}. Strategist requests {Requests}, proposals {Proposals}; {Activations} intents took effect ({Renewals} renewals), {PostureFlips} posture flips, {Pivots.Count} pivots, {Rejected.Count} proposals without effect.");
        sb.AppendLine();
        sb.AppendLine("## Intent timeline");
        sb.AppendLine();
        sb.AppendLine("| From s | To s | Playbook | Posture | Source (role) | Confidence | Trigger | Why it took effect | How it ended | Renewals | Rationale |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (TimelineEntry e in Timeline)
        {
            sb.AppendLine($"| {F(e.AtSeconds, "0")} | {(e.EndedAtSeconds is { } x ? F(x, "0") : "end")} | {e.PlaybookId} | {e.Posture} | {e.Source} ({e.Role}) | {F(e.Confidence, "0.00")} | {e.Trigger ?? "–"} | {e.ActivationReason} | {e.EndReason ?? "–"} | {e.Renewals} | {Cell(e.Rationale)} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Pivots");
        sb.AppendLine();
        if (Pivots.Count == 0) sb.AppendLine("None: one playbook and posture throughout.");
        foreach (Pivot p in Pivots)
        {
            sb.AppendLine($"- **{F(p.AtSeconds, "0")} s**: {p.FromPlaybook} ({p.FromPosture}) → {p.ToPlaybook} ({p.ToPosture}). Trigger: {p.Trigger ?? "none recorded"}; arbiter: {p.ActivationReason}; previous intent ended: {p.PreviousEndReason ?? "–"}. Rationale: {p.Rationale ?? "–"}");
        }
        sb.AppendLine();
        sb.AppendLine("## Proposals without effect");
        sb.AppendLine();
        if (Rejected.Count == 0) sb.AppendLine("None.");
        foreach (IGrouping<string, RejectedProposal> group in Rejected.GroupBy(static r => $"{r.Outcome}: {string.Join(", ", r.Codes)}").OrderByDescending(static g => g.Count()).ThenBy(static g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"- {group.Key} × {group.Count()} (first at {F(group.First().AtSeconds, "0")} s, {string.Join(", ", group.Select(static r => r.StrategistId).Distinct(StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal))})");
        }
        sb.AppendLine();
        sb.AppendLine("## Key events");
        sb.AppendLine();
        if (KeyEvents.Count == 0) sb.AppendLine("None.");
        foreach (KeyEvent e in KeyEvents) sb.AppendLine($"- {F(e.AtSeconds, "0")} s {e.Kind}: {e.Detail}");
        sb.AppendLine();
        sb.AppendLine("## Shadow strategist");
        sb.AppendLine();
        if (Shadow.Proposals == 0)
        {
            sb.AppendLine("No shadow proposals in this log.");
        }
        else
        {
            sb.AppendLine($"{Shadow.Proposals} shadow proposals; {Shadow.Compared} had a primary proposal for the same request. Same playbook: {Shadow.Agreed}/{Shadow.Compared} ({(Shadow.AgreementRate is { } a ? F(a, "0.000") : "n/a")}); same posture: {Shadow.PostureAgreed}/{Shadow.Compared}. The arbiter would have activated or renewed {Shadow.WouldHaveActivated}.");
            sb.AppendLine($"Shadow playbooks: {string.Join(", ", Shadow.ShadowPlaybooks.Select(static p => $"{p.Key} × {p.Value}"))}.");
        }
        sb.AppendLine();
        sb.AppendLine("## Time by playbook and posture");
        sb.AppendLine();
        sb.AppendLine($"Playbooks: {string.Join(", ", PlaybookSeconds.Select(static p => $"{p.Key} {F(p.Value, "0")} s"))}.");
        sb.AppendLine($"Postures: {string.Join(", ", PostureSeconds.Select(static p => $"{p.Key} {F(p.Value, "0")} s"))}.");
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(this, BotJson.Options);

    private static string Note(JsonElement notes, string prefix) =>
        notes.EnumerateArray().Select(static n => n.GetString() ?? string.Empty).FirstOrDefault(n => n.StartsWith(prefix, StringComparison.Ordinal)) ?? prefix;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cell(string? text) => text is null ? "–" : text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
