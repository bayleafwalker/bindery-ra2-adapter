// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter;

public sealed record CncNetTunnelAttachment(RelayPlacement Placement, string Host, int Port) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Validates and records a coordinator-issued private CnCNet endpoint. The
/// actual CnCNet-compatible relay process is deliberately external to this
/// library; this boundary must not silently substitute Bindery's native wire
/// protocol.
/// </summary>
public sealed class CncNetPrivateTunnelBoundary
{
    public Task<CncNetTunnelAttachment> AttachAsync(RelayPlacement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(placement.RelayProviderId, RelaySelection.ProviderName(RelayProvider.CncNetPrivate), StringComparison.Ordinal))
            throw new InvalidOperationException("private CnCNet attachment requires the cncnet-private placement provider");
        (string host, int port) = TwoClientMatchDriver.ParseRelayEndpoint(placement.RelayEndpoint);
        return Task.FromResult(new CncNetTunnelAttachment(placement, host, port));
    }
}

public sealed class CncNetPrivateMatchDriver : ILiveMatchDriver
{
    private readonly TwoClientMatchDriver enrollmentDriver;
    private readonly CncNetPrivateTunnelBoundary tunnelBoundary;

    public CncNetPrivateMatchDriver(BinderyAdapterClient controlPlane, Uri serviceUri, CncNetPrivateTunnelBoundary? tunnelBoundary = null)
    {
        enrollmentDriver = new TwoClientMatchDriver(controlPlane, serviceUri);
        this.tunnelBoundary = tunnelBoundary ?? new CncNetPrivateTunnelBoundary();
    }

    public async Task<PreparedLiveMatch> PrepareLiveAsync(
        SessionCreationRequest sessionRequest,
        MatchClientDefinition first,
        MatchClientDefinition second,
        string sessionIdempotencyKey,
        string firstEnrollmentIdempotencyKey,
        string secondEnrollmentIdempotencyKey,
        CancellationToken cancellationToken = default)
    {
        PreparedEnrollmentPair pair = await enrollmentDriver.PrepareEnrollmentsAsync(
            sessionRequest,
            first,
            second,
            RelayProvider.CncNetPrivate,
            sessionIdempotencyKey,
            firstEnrollmentIdempotencyKey,
            secondEnrollmentIdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        CncNetTunnelAttachment? firstAttachment = null;
        try
        {
            firstAttachment = await tunnelBoundary.AttachAsync(pair.Session.Placement!, cancellationToken).ConfigureAwait(false);
            CncNetTunnelAttachment secondAttachment = await tunnelBoundary.AttachAsync(pair.Session.Placement!, cancellationToken).ConfigureAwait(false);
            return new PreparedLiveMatch(
                pair.Session,
                new PreparedLiveClient(first, pair.First.Enrollment, pair.First.Configuration, firstAttachment.Placement),
                new PreparedLiveClient(second, pair.Second.Enrollment, pair.Second.Configuration, secondAttachment.Placement),
                new CncNetPrivateMatch(firstAttachment, secondAttachment));
        }
        catch
        {
            if (firstAttachment is not null) await firstAttachment.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class CncNetPrivateMatch(CncNetTunnelAttachment first, CncNetTunnelAttachment second) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await first.DisposeAsync().ConfigureAwait(false);
            await second.DisposeAsync().ConfigureAwait(false);
        }
    }
}
