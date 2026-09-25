// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Bindery.Ra2.Adapter.Bot;

/// <summary>
/// The wire shape for one command sent to retail RA2/YR: a schema version, a
/// stable <see cref="Kind"/> string, the issuing controller (for provenance
/// and post-hoc log correlation with <see cref="Bindery.Ra2.Bot.DecisionRecord"/>-style
/// records), and command-specific <see cref="Fields"/> serialised with
/// <see cref="Bindery.Ra2.Bot.BotJson.Options"/>. Versioned as
/// <c>bindery.ra2.bot-command/v1</c>; <see cref="Ra2CommandSink"/> is the only
/// place that builds one, from a <see cref="Bindery.Ra2.Bot.GameCommand"/>.
/// </summary>
public sealed record Ra2CommandEnvelope(string SchemaVersion, string Kind, string Controller, JsonElement Fields)
{
    public const string CurrentSchemaVersion = "bindery.ra2.bot-command/v1";
}

/// <summary>
/// Deliberate seam for the native ra2yrcpp fork's command RPC, mirroring the
/// telemetry seam (<c>IRa2TelemetrySource</c>) style: this repository decodes
/// and buffers envelopes, but does not invent a second RPC client or vendor
/// the native fork's generated protobuf/TCP framing. A native implementation
/// belongs to the ra2yrcpp fork and is out of scope for this package.
/// </summary>
public interface IRa2CommandTransport
{
    Task SendAsync(Ra2CommandEnvelope envelope, CancellationToken cancellationToken = default);
}
