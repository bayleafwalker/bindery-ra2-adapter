// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Bot;
using Bindery.Ra2.Bot.Runtime;

namespace Bindery.Ra2.Adapter.Bot;

/// <summary>What one <see cref="Ra2BotHost.RunAsync"/> call did.</summary>
/// <param name="RawEvents">Raw telemetry events read from the source.</param>
/// <param name="Frames">Observation frames the bot ticked.</param>
/// <param name="CommandsSent">Command envelopes handed to the transport.</param>
/// <param name="MatchEnded">True when the loop stopped on a <see cref="GameEventKind.MatchEnded"/> frame; false when the source ran out or the run was cancelled.</param>
/// <param name="MissingFields">Every contract field the telemetry did not carry (see <see cref="Ra2ObservationAssembler.MissingFields"/>).</param>
public sealed record Ra2BotHostReport(long RawEvents, long Frames, long CommandsSent, bool MatchEnded, MissingFieldsReport MissingFields);

/// <summary>One ticked frame and the commands the bot produced for it.</summary>
public sealed record Ra2BotHostFrame(ObservationFrame Frame, IReadOnlyList<GameCommand> Commands);

/// <summary>
/// The retail host loop: <see cref="IRa2TelemetrySource"/> → <see cref="Ra2Normalizer"/> →
/// <see cref="Ra2ObservationAssembler"/> → <see cref="BotRuntime.Tick"/> → <see cref="Ra2CommandSink"/> →
/// <see cref="IRa2CommandTransport"/>. It is the only piece between the ra2yrcpp seams and the bot, so a
/// native telemetry source and a native command transport are all a retail match still needs.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Raw events are normalized one at a time in arrival order (the source is a live stream; the
/// batch normalizer's sequence sort would only hold the stream back).</item>
/// <item>The bot ticks once per assembled frame, and that frame's commands are flushed to the transport before
/// the next frame is ticked, so commands reach the game in decision order and never pile up behind a slow frame.</item>
/// <item>The loop ends on the first frame carrying <see cref="GameEventKind.MatchEnded"/>, when the source
/// completes, or on cancellation. Nothing after the end of the match is read.</item>
/// <item>What the telemetry contract (<c>bindery.ra2.bot-observation/v1</c>) does not carry is not invented:
/// production queue state, visible regions beyond the observed entities, ore and superweapon timers are absent
/// from assembled frames, and the planner treats queues as empty. The report carries the assembler's
/// missing-field audit.</item>
/// </list>
/// The loop is single-threaded: <see cref="BotRuntime"/> is not thread-safe and needs one caller.
/// </remarks>
public sealed class Ra2BotHost
{
    private readonly BotRuntime runtime;
    private readonly Ra2ObservationAssembler assembler;
    private readonly Ra2CommandSink sink;
    private readonly Ra2Normalizer normalizer;

    public Ra2BotHost(BotRuntime runtime, Ra2ObservationAssembler assembler, Ra2CommandSink sink, Ra2Normalizer? normalizer = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(assembler);
        ArgumentNullException.ThrowIfNull(sink);
        this.runtime = runtime;
        this.assembler = assembler;
        this.sink = sink;
        this.normalizer = normalizer ?? new Ra2Normalizer();
    }

    /// <summary>Raised after each frame's commands were flushed; for diagnostics and tests.</summary>
    public event EventHandler<Ra2BotHostFrame>? FrameTicked;

    public async Task<Ra2BotHostReport> RunAsync(IRa2TelemetrySource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        long raw = 0, frames = 0, sent = 0;
        bool ended = false;
        await foreach (RawObservation observation in source.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            raw++;
            foreach (NormalizedObservation normalized in normalizer.Normalize([observation]))
            {
                foreach (ObservationFrame frame in assembler.Ingest(normalized))
                {
                    IReadOnlyList<GameCommand> commands = runtime.Tick(frame);
                    foreach (GameCommand command in commands) sink.Submit(command);
                    await sink.FlushAsync(cancellationToken).ConfigureAwait(false);
                    frames++;
                    sent += commands.Count;
                    FrameTicked?.Invoke(this, new Ra2BotHostFrame(frame, commands));
                    if (frame.Events.Any(static e => e.Kind == GameEventKind.MatchEnded))
                    {
                        ended = true;
                        break;
                    }
                }
                if (ended) break;
            }
            if (ended) break;
        }
        return new Ra2BotHostReport(raw, frames, sent, ended, assembler.MissingFields);
    }
}
