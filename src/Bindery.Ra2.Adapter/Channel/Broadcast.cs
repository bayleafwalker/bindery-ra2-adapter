// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter.Channel;

public enum BroadcastDestinationKind
{
    /// <summary>The room stream: OBS publishes to a local MediaMTX path.</summary>
    LocalRoom,
    /// <summary>The public output, relayed from the same production.</summary>
    Twitch,
}

/// <summary>
/// One place the production goes.
/// </summary>
/// <remarks>
/// A stream key is never part of this record. <paramref name="StreamKeyEnvironmentVariable"/>
/// names the variable the relay reads it from, so the key stays out of
/// channel records, evidence and logs.
/// </remarks>
public sealed record BroadcastDestination(
    string Name,
    BroadcastDestinationKind Kind,
    Uri Endpoint,
    string? StreamKeyEnvironmentVariable = null);

/// <summary>
/// The scenes the channel switches between. They must already exist in the
/// OBS scene collection; the channel only selects them.
/// </summary>
public sealed record BroadcastScenes(string Match = "ra2-match", string Holding = "ra2-holding");

/// <summary>
/// The production for one channel: one scene collection, a local room stream
/// that is always present, and at most one public destination.
/// </summary>
public sealed record BroadcastPlan(
    IReadOnlyList<BroadcastDestination> Destinations,
    BroadcastScenes? Scenes = null,
    bool PublishPublicly = false)
{
    public BroadcastScenes EffectiveScenes => Scenes ?? new BroadcastScenes();

    public BroadcastDestination LocalRoom => Destinations.Single(static d => d.Kind == BroadcastDestinationKind.LocalRoom);

    public BroadcastDestination? Public => PublishPublicly
        ? Destinations.SingleOrDefault(static d => d.Kind == BroadcastDestinationKind.Twitch)
        : null;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Destinations);
        foreach (BroadcastDestination destination in Destinations)
        {
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentException.ThrowIfNullOrWhiteSpace(destination.Name);
            ArgumentNullException.ThrowIfNull(destination.Endpoint);
            if (!destination.Endpoint.IsAbsoluteUri) throw new ArgumentException($"{destination.Name} needs an absolute endpoint");
            // A key pasted into the endpoint would end up in every record.
            if (destination.Endpoint.Segments.Any(static s => s.TrimEnd('/').StartsWith("live_", StringComparison.Ordinal)))
                throw new ArgumentException($"{destination.Name} carries what looks like a stream key; reference it by environment variable instead");
        }
        if (Destinations.Count(static d => d.Kind == BroadcastDestinationKind.LocalRoom) != 1)
            throw new ArgumentException("a channel has exactly one local room stream; it is the primary playback path");
        if (Destinations.Count(static d => d.Kind == BroadcastDestinationKind.Twitch) > 1)
            throw new ArgumentException("a channel has at most one public destination");
        BroadcastDestination? twitch = Destinations.SingleOrDefault(static d => d.Kind == BroadcastDestinationKind.Twitch);
        if (PublishPublicly && twitch is null) throw new ArgumentException("public publishing needs a Twitch destination");
        if (twitch is not null && string.IsNullOrWhiteSpace(twitch.StreamKeyEnvironmentVariable))
            throw new ArgumentException("the Twitch destination must name the environment variable holding its stream key");
        BroadcastScenes scenes = EffectiveScenes;
        ArgumentException.ThrowIfNullOrWhiteSpace(scenes.Match);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenes.Holding);
        if (string.Equals(scenes.Match, scenes.Holding, StringComparison.Ordinal))
            throw new ArgumentException("the match and holding scenes must differ");
    }
}

/// <summary>
/// The capture-and-broadcast boundary. OBS captures the selected rendered
/// client and publishes it; this interface only drives it.
/// </summary>
public interface IBroadcastProduction
{
    /// <summary>Start publishing to the local room (and the public relay, when enabled).</summary>
    Task StartAsync(BroadcastPlan plan, CancellationToken cancellationToken);

    Task ShowHoldingAsync(string reason, CancellationToken cancellationToken);

    Task ShowMatchAsync(CaptureSource source, CancellationToken cancellationToken);

    /// <summary>Stop publishing. Must be safe to call when nothing was started.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The rollback: run the channel loop without publishing anything.
/// </summary>
public sealed class NoBroadcastProduction : IBroadcastProduction
{
    public Task StartAsync(BroadcastPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShowHoldingAsync(string reason, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ShowMatchAsync(CaptureSource source, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
