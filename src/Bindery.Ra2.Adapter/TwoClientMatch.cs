// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter;

public sealed record MatchClientDefinition(
    IdentityCredentials Identity,
    string ClientInstanceId,
    ClientClass ClientClass,
    AdapterIdentity Adapter,
    CompatibilityHashes Compatibility,
    IReadOnlyList<RegionProbe>? RegionProbes = null);

public sealed record PreparedMatchClient(
    MatchClientDefinition Definition,
    EnrollmentCredentials Enrollment,
    AdapterConfiguration Configuration,
    BinderyRelayClient Relay);

/// <summary>
/// The common live-runner shape. Native and private CnCNet transports both
/// produce coordinator-issued endpoint/configuration data, but only the
/// native fixture creates a <see cref="BinderyRelayClient"/> in this process.
/// </summary>
public sealed record PreparedLiveClient(
    MatchClientDefinition Definition,
    EnrollmentCredentials Enrollment,
    AdapterConfiguration Configuration,
    RelayPlacement Placement);

public sealed class PreparedLiveMatch(
    SessionCredentials session,
    PreparedLiveClient first,
    PreparedLiveClient second,
    IAsyncDisposable owner) : IAsyncDisposable
{
    public SessionCredentials Session { get; } = session;
    public PreparedLiveClient First { get; } = first;
    public PreparedLiveClient Second { get; } = second;

    public ValueTask DisposeAsync() => owner.DisposeAsync();

    internal static PreparedLiveMatch FromNative(TwoClientMatch match) => new(
        match.Session,
        new PreparedLiveClient(match.First.Definition, match.First.Enrollment, match.First.Configuration, match.First.Relay.Placement),
        new PreparedLiveClient(match.Second.Definition, match.Second.Enrollment, match.Second.Configuration, match.Second.Relay.Placement),
        match);
}

public interface ILiveMatchDriver
{
    Task<PreparedLiveMatch> PrepareLiveAsync(
        SessionCreationRequest sessionRequest,
        MatchClientDefinition first,
        MatchClientDefinition second,
        string sessionIdempotencyKey,
        string firstEnrollmentIdempotencyKey,
        string secondEnrollmentIdempotencyKey,
        CancellationToken cancellationToken = default);
}

public sealed class TwoClientMatch(
    SessionCredentials session,
    PreparedMatchClient first,
    PreparedMatchClient second) : IAsyncDisposable
{
    public SessionCredentials Session { get; } = session;
    public PreparedMatchClient First { get; } = first;
    public PreparedMatchClient Second { get; } = second;

    public async ValueTask DisposeAsync()
    {
        await First.Relay.DisposeAsync().ConfigureAwait(false);
        await Second.Relay.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Prepares two distinct player clients against one coordinator-issued native
/// relay allocation. This remains a protocol fixture; the live profile uses
/// <see cref="CncNetPrivateMatchDriver"/>.
/// </summary>
public sealed class TwoClientMatchDriver : ILiveMatchDriver
{
    private readonly BinderyAdapterClient controlPlane;
    private readonly Uri serviceUri;

    public TwoClientMatchDriver(BinderyAdapterClient controlPlane, Uri serviceUri)
    {
        this.controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
        this.serviceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
    }

    public async Task<TwoClientMatch> PrepareAsync(
        SessionCreationRequest sessionRequest,
        MatchClientDefinition first,
        MatchClientDefinition second,
        string sessionIdempotencyKey,
        string firstEnrollmentIdempotencyKey,
        string secondEnrollmentIdempotencyKey,
        CancellationToken cancellationToken = default)
    {
        PreparedEnrollmentPair pair = await PrepareEnrollmentsAsync(
            sessionRequest,
            first,
            second,
            RelayProvider.BinderyNative,
            sessionIdempotencyKey,
            firstEnrollmentIdempotencyKey,
            secondEnrollmentIdempotencyKey,
            cancellationToken).ConfigureAwait(false);

        BinderyRelayClient? firstRelay = null;
        try
        {
            firstRelay = await controlPlane.ConnectRelayAsync(pair.First.Configuration, pair.Session, cancellationToken).ConfigureAwait(false);
            BinderyRelayClient secondRelay = await controlPlane.ConnectRelayAsync(pair.Second.Configuration, pair.Session, cancellationToken).ConfigureAwait(false);
            return new TwoClientMatch(
                pair.Session,
                new PreparedMatchClient(first, pair.First.Enrollment, pair.First.Configuration, firstRelay),
                new PreparedMatchClient(second, pair.Second.Enrollment, pair.Second.Configuration, secondRelay));
        }
        catch
        {
            if (firstRelay is not null) await firstRelay.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
        TwoClientMatch match = await PrepareAsync(
            sessionRequest,
            first,
            second,
            sessionIdempotencyKey,
            firstEnrollmentIdempotencyKey,
            secondEnrollmentIdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        return PreparedLiveMatch.FromNative(match);
    }

    internal async Task<PreparedEnrollmentPair> PrepareEnrollmentsAsync(
        SessionCreationRequest sessionRequest,
        MatchClientDefinition first,
        MatchClientDefinition second,
        RelayProvider expectedProvider,
        string sessionIdempotencyKey,
        string firstEnrollmentIdempotencyKey,
        string secondEnrollmentIdempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateInputs(sessionRequest, first, second, sessionIdempotencyKey, firstEnrollmentIdempotencyKey, secondEnrollmentIdempotencyKey);

        SessionCredentials session = await controlPlane.CreateSessionAsync(
            first.Identity.AccountToken,
            sessionRequest,
            sessionIdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (session.Placement is null) throw new InvalidOperationException("two-client match requires a coordinator-issued relay placement");
        if (!string.Equals(session.Placement.RelayProviderId, RelaySelection.ProviderName(expectedProvider), StringComparison.Ordinal))
            throw new InvalidOperationException($"two-client match requires the {RelaySelection.ProviderName(expectedProvider)} relay provider");

        EnrollmentCredentials firstEnrollment = await controlPlane.EnrollAsync(
            ToEnrollmentRequest(first, session),
            firstEnrollmentIdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        EnrollmentCredentials secondEnrollment = await controlPlane.EnrollAsync(
            ToEnrollmentRequest(second, session),
            secondEnrollmentIdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(firstEnrollment.ClientId, secondEnrollment.ClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("two-client enrollment returned the same client id twice");

        return new PreparedEnrollmentPair(
            session,
            new PreparedEnrollmentClient(firstEnrollment, ToConfiguration(first, session, firstEnrollment, expectedProvider)),
            new PreparedEnrollmentClient(secondEnrollment, ToConfiguration(second, session, secondEnrollment, expectedProvider)));
    }

    private static ClientEnrollmentRequest ToEnrollmentRequest(MatchClientDefinition definition, SessionCredentials session) => new(
        definition.Identity.AccountToken,
        session.SessionJoinCredential,
        session.SessionId,
        definition.ClientInstanceId,
        definition.ClientClass,
        definition.Adapter,
        definition.Compatibility,
        definition.RegionProbes);

    private AdapterConfiguration ToConfiguration(MatchClientDefinition definition, SessionCredentials session, EnrollmentCredentials enrollment, RelayProvider provider)
    {
        (string host, int port) = ParseRelayEndpoint(session.Placement!.RelayEndpoint);
        return new AdapterConfiguration(
            serviceUri,
            definition.Identity.AccountToken,
            session.SessionJoinCredential,
            session.SessionId,
            definition.ClientInstanceId,
            enrollment.ClientId,
            enrollment.ClientLeaseToken,
            definition.ClientClass,
            definition.Adapter,
            definition.Compatibility,
            provider,
            host,
            port,
            provider == RelayProvider.BinderyNative ? enrollment.TransportCredential : null);
    }

    internal static (string Host, int Port) ParseRelayEndpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("relay placement endpoint is required");
        int separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1 || !int.TryParse(value[(separator + 1)..], out int port) || port is < 1 or > 65535)
            throw new InvalidOperationException("relay placement endpoint must be host:port");
        string host = value[..separator].Trim('[', ']');
        return (host, port);
    }

    private static void ValidateInputs(
        SessionCreationRequest sessionRequest,
        MatchClientDefinition first,
        MatchClientDefinition second,
        string sessionIdempotencyKey,
        string firstEnrollmentIdempotencyKey,
        string secondEnrollmentIdempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(sessionRequest);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        ArgumentNullException.ThrowIfNull(first.Identity);
        ArgumentNullException.ThrowIfNull(second.Identity);
        if (sessionRequest.ParticipantPolicy.RequiredPlayers != 2 || sessionRequest.ParticipantPolicy.MaximumPlayers < 2)
            throw new InvalidOperationException("two-client match requires a two-player participant policy");
        if (first.ClientClass != ClientClass.Player || second.ClientClass != ClientClass.Player)
            throw new InvalidOperationException("two-client match requires two player clients");
        if (string.Equals(first.Identity.AccountId, second.Identity.AccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("two-client match requires distinct account identities");
        if (string.Equals(first.ClientInstanceId, second.ClientInstanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("two-client match requires distinct client instances");
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstEnrollmentIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondEnrollmentIdempotencyKey);
        if (string.Equals(firstEnrollmentIdempotencyKey, secondEnrollmentIdempotencyKey, StringComparison.Ordinal))
            throw new InvalidOperationException("two-client enrollment idempotency keys must be distinct");
    }
}

internal sealed record PreparedEnrollmentClient(EnrollmentCredentials Enrollment, AdapterConfiguration Configuration);

internal sealed record PreparedEnrollmentPair(
    SessionCredentials Session,
    PreparedEnrollmentClient First,
    PreparedEnrollmentClient Second);
