// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bindery.Ra2.Adapter;

public sealed class BinderyAdapterClient(HttpClient httpClient)
{
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    public async Task<LifecycleReportResult> ReportAsync(AdapterConfiguration configuration, LifecycleReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(report);
        using HttpRequestMessage request = new(HttpMethod.Post, $"/v1/enrollments/{configuration.ClientId}/reports")
        {
            Content = JsonContent.Create(new LifecycleReportRequest(
                report.ReportId,
                LifecycleKindName(report.Kind),
                report.Reason), options: json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ClientLeaseToken);
        request.Headers.Add("Idempotency-Key", report.ReportId);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        LifecycleReportResponseDto dto = await response.Content.ReadFromJsonAsync<LifecycleReportResponseDto>(json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("lifecycle report response was empty");
        EnsureIdentifier(configuration.SessionId, dto.PublicSession.SessionId, "session");
        EnsureIdentifier(configuration.ClientId, dto.PublicEnrollment.ClientId, "enrollment");
        return new LifecycleReportResult(ToSessionStatus(dto.PublicSession), ToEnrollmentStatus(dto.PublicEnrollment));
    }

    public async Task<IdentityCredentials> CreateIdentityAsync(string handle, string? displayName, string idempotencyKey, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/identities")
        {
            Content = JsonContent.Create(new IdentityCreationRequest(handle, displayName), options: json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        IdentityCreateDto dto = await response.Content.ReadFromJsonAsync<IdentityCreateDto>(json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("identity response was empty");
        return new IdentityCredentials(dto.PublicIdentity.AccountId, dto.AccountToken);
    }

    public async Task<SessionCredentials> CreateSessionAsync(string accountToken, SessionCreationRequest sessionRequest, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountToken);
        ArgumentNullException.ThrowIfNull(sessionRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/sessions")
        {
            Content = JsonContent.Create(sessionRequest, options: json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        SessionCreateDto dto = await response.Content.ReadFromJsonAsync<SessionCreateDto>(json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("session response was empty");
        RelayPlacement? placement = dto.PublicSession.Placement is null ? null : ToRelayPlacement(dto.PublicSession.Placement);
        // The join credential is a one-time secret: it is returned when the
        // session is created and never again. An idempotency key that has
        // already been used replays the public session without it, which then
        // fails at enrollment with a confusing "empty credential" error rather
        // than naming the real cause.
        if (string.IsNullOrWhiteSpace(dto.SessionJoinCredential))
            throw new InvalidOperationException(
                $"session '{dto.PublicSession.SessionId}' came back without a join credential, which means this idempotency key was already used; " +
                "use a fresh session idempotency key for each new match");
        return new SessionCredentials(dto.PublicSession.SessionId, dto.SessionJoinCredential, placement);
    }

    public async Task<SessionStatus> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        RequireUuid(sessionId, "session id");
        using HttpResponseMessage response = await httpClient.GetAsync($"/v1/sessions/{sessionId}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        SessionStatusDto dto = await response.Content.ReadFromJsonAsync<SessionStatusDto>(json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("session response was empty");
        EnsureIdentifier(sessionId, dto.SessionId, "session");
        return ToSessionStatus(dto);
    }

    public async Task<EnrollmentStatus> GetEnrollmentAsync(string clientId, CancellationToken cancellationToken = default)
    {
        RequireUuid(clientId, "client id");
        using HttpResponseMessage response = await httpClient.GetAsync($"/v1/enrollments/{clientId}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnrollmentStatusDto dto = await response.Content.ReadFromJsonAsync<EnrollmentStatusDto>(json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("enrollment response was empty");
        EnsureIdentifier(clientId, dto.ClientId, "enrollment");
        return ToEnrollmentStatus(dto);
    }

    public Task<BinderyRelayClient> ConnectRelayAsync(AdapterConfiguration configuration, SessionCredentials session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(session);
        RelaySelection.Validate(configuration);
        if (!string.Equals(configuration.SessionId, session.SessionId, StringComparison.Ordinal)) throw new InvalidOperationException("session credentials do not match adapter configuration");
        if (configuration.RelayProvider != RelayProvider.BinderyNative) throw new InvalidOperationException("CnCNet transports do not use the Bindery relay client");
        if (session.Placement is null) throw new InvalidOperationException("session response did not contain a relay placement");
        if (!string.Equals(session.Placement.RelayProviderId, RelaySelection.ProviderName(RelayProvider.BinderyNative), StringComparison.Ordinal)) throw new InvalidOperationException("session placement selected a different relay provider");
        return BinderyRelayClient.ConnectAsync(session.Placement, configuration.ClientId, configuration.RelayCredential!, cancellationToken);
    }

    public Task<EnrollmentCredentials> EnrollAsync(AdapterConfiguration configuration, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return EnrollAsync(
            new ClientEnrollmentRequest(
                configuration.AccountToken,
                configuration.SessionJoinCredential,
                configuration.SessionId,
                configuration.ClientInstanceId,
                configuration.ClientClass,
                configuration.Adapter,
                configuration.Compatibility),
            idempotencyKey,
            cancellationToken);
    }

    public async Task<EnrollmentCredentials> EnrollAsync(ClientEnrollmentRequest enrollmentRequest, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enrollmentRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentRequest.AccountToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentRequest.SessionJoinCredential);
        RequireUuid(enrollmentRequest.SessionId, "session id");
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        EnrollmentRequestDto requestBody = new(
            enrollmentRequest.ClientInstanceId,
            ClientClassName(enrollmentRequest.ClientClass),
            new AdapterRequestDto(enrollmentRequest.Adapter.Id, enrollmentRequest.Adapter.Version),
            new ClientHashesRequestDto(enrollmentRequest.Compatibility.GameHash, enrollmentRequest.Compatibility.ModHash, enrollmentRequest.Compatibility.MapHash),
            enrollmentRequest.RegionProbes);
        using HttpRequestMessage request = new(HttpMethod.Post, $"/v1/sessions/{enrollmentRequest.SessionId}/enrollments")
        {
            Content = JsonContent.Create(requestBody, options: json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", enrollmentRequest.AccountToken);
        request.Headers.Add("X-Session-Join-Credential", enrollmentRequest.SessionJoinCredential);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        EnrollmentCreateDto dto = await response.Content.ReadFromJsonAsync<EnrollmentCreateDto>(json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("enrollment response was empty");
        return new EnrollmentCredentials(dto.PublicEnrollment.ClientId, dto.ClientLeaseToken, dto.TransportCredential);
    }

    public async Task<DateTimeOffset> HeartbeatAsync(AdapterConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        RequireUuid(configuration.ClientId, "client id");
        using HttpRequestMessage request = new(HttpMethod.Post, $"/v1/enrollments/{configuration.ClientId}:heartbeat");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ClientLeaseToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        HeartbeatResponseDto dto = await response.Content.ReadFromJsonAsync<HeartbeatResponseDto>(json, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("heartbeat response was empty");
        EnsureIdentifier(configuration.ClientId, dto.ClientId, "enrollment");
        return dto.ExpiresAt;
    }

    private static string LifecycleKindName(LifecycleKind kind) => kind switch
    {
        LifecycleKind.Ready => "ready",
        LifecycleKind.Started => "started",
        LifecycleKind.Exited => "exited",
        LifecycleKind.Failed => "failed",
        LifecycleKind.CaptureDegraded => "capture_degraded",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unsupported lifecycle kind"),
    };

    private static SessionStatus ToSessionStatus(SessionStatusDto dto) => new(
        dto.SessionId,
        ParseSessionPhase(dto.Phase),
        dto.Placement is null ? null : ToRelayPlacement(dto.Placement),
        dto.Enrollments.Select(ToEnrollmentStatus).ToArray());

    private static EnrollmentStatus ToEnrollmentStatus(EnrollmentStatusDto dto) => new(
        dto.ClientId,
        dto.AccountId,
        ParseClientClass(dto.ClientClass),
        ParseEnrollmentPhase(dto.Phase),
        dto.AdapterId,
        dto.AdapterVersion);

    private static RelayPlacement ToRelayPlacement(PublicPlacementDto placement) => new(
        placement.Region,
        placement.RelayProviderId,
        placement.RelayAllocationId,
        placement.RelayEndpoint,
        placement.PolicyVersion,
        placement.DecisionSummary);

    private static SessionPhase ParseSessionPhase(string phase) => phase switch
    {
        "created" => SessionPhase.Created,
        "admitting" => SessionPhase.Admitting,
        "ready" => SessionPhase.Ready,
        "running" => SessionPhase.Running,
        "ended" => SessionPhase.Ended,
        "failed" => SessionPhase.Failed,
        "expired" => SessionPhase.Expired,
        "published" => SessionPhase.Published,
        _ => throw new InvalidOperationException($"control plane returned unsupported session phase '{phase}'"),
    };

    private static EnrollmentPhase ParseEnrollmentPhase(string phase) => phase switch
    {
        "issued" => EnrollmentPhase.Issued,
        "registered" => EnrollmentPhase.Registered,
        "ready" => EnrollmentPhase.Ready,
        "active" => EnrollmentPhase.Active,
        "departed" => EnrollmentPhase.Departed,
        "lost" => EnrollmentPhase.Lost,
        "expired" => EnrollmentPhase.Expired,
        _ => throw new InvalidOperationException($"control plane returned unsupported enrollment phase '{phase}'"),
    };

    private static ClientClass ParseClientClass(string clientClass) => clientClass switch
    {
        "player" => ClientClass.Player,
        "observer" => ClientClass.Observer,
        _ => throw new InvalidOperationException($"control plane returned unsupported client class '{clientClass}'"),
    };

    private static string ClientClassName(ClientClass clientClass) => clientClass switch
    {
        ClientClass.Player => "player",
        ClientClass.Observer => "observer",
        _ => throw new ArgumentOutOfRangeException(nameof(clientClass), clientClass, "unsupported client class"),
    };

    private static void RequireUuid(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParseExact(value, "D", out _)) throw new ArgumentException($"{label} must be a canonical UUID", label);
    }

    private static void EnsureIdentifier(string expected, string actual, string label)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new InvalidOperationException($"control plane returned a different {label} than requested");
    }
}

internal sealed record PublicIdentityDto([property: JsonPropertyName("account_id")] string AccountId);
public sealed record IdentityCredentials(string AccountId, string AccountToken);
public sealed record SessionCredentials(string SessionId, string SessionJoinCredential, RelayPlacement? Placement = null);
public sealed record EnrollmentCredentials(string ClientId, string ClientLeaseToken, string TransportCredential);

internal sealed record IdentityCreateDto([property: JsonPropertyName("public_identity")] PublicIdentityDto PublicIdentity, [property: JsonPropertyName("account_token")] string AccountToken);
internal sealed record SessionCreateDto([property: JsonPropertyName("public_session")] SessionPublicDto PublicSession, [property: JsonPropertyName("session_join_credential")] string SessionJoinCredential);
internal sealed record SessionPublicDto([property: JsonPropertyName("session_id")] string SessionId, [property: JsonPropertyName("placement")] PublicPlacementDto? Placement);
internal sealed record PublicPlacementDto(
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("relay_provider_id")] string RelayProviderId,
    [property: JsonPropertyName("relay_allocation_id")] string RelayAllocationId,
    [property: JsonPropertyName("relay_endpoint")] string RelayEndpoint,
    [property: JsonPropertyName("policy_version")] string PolicyVersion,
    [property: JsonPropertyName("decision_summary")] string? DecisionSummary);
internal sealed record EnrollmentCreateDto([property: JsonPropertyName("public_enrollment")] EnrollmentPublicDto PublicEnrollment, [property: JsonPropertyName("client_lease_token")] string ClientLeaseToken, [property: JsonPropertyName("transport_credential")] string TransportCredential);
internal sealed record EnrollmentPublicDto([property: JsonPropertyName("client_id")] string ClientId);
internal sealed record EnrollmentRequestDto(
    [property: JsonPropertyName("client_instance_id")] string ClientInstanceId,
    [property: JsonPropertyName("client_class")] string ClientClass,
    [property: JsonPropertyName("adapter")] AdapterRequestDto Adapter,
    [property: JsonPropertyName("compatibility")] ClientHashesRequestDto Compatibility,
    [property: JsonPropertyName("region_probes")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RegionProbe>? RegionProbes);

internal sealed record AdapterRequestDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version);

internal sealed record ClientHashesRequestDto(
    [property: JsonPropertyName("game_hash")] string GameHash,
    [property: JsonPropertyName("mod_hash")] string ModHash,
    [property: JsonPropertyName("map_hash")] string MapHash);

internal sealed record HeartbeatResponseDto(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
internal sealed record LifecycleReportResponseDto(
    [property: JsonPropertyName("public_session")] SessionStatusDto PublicSession,
    [property: JsonPropertyName("public_enrollment")] EnrollmentStatusDto PublicEnrollment);

internal sealed record SessionStatusDto(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("placement")] PublicPlacementDto? Placement,
    [property: JsonPropertyName("enrollments")] IReadOnlyList<EnrollmentStatusDto> Enrollments);

internal sealed record EnrollmentStatusDto(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("client_class")] string ClientClass,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("adapter_id")] string AdapterId,
    [property: JsonPropertyName("adapter_version")] string AdapterVersion);

internal sealed record LifecycleReportRequest(
    [property: JsonPropertyName("report_id")] string ReportId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason);

internal sealed record IdentityCreationRequest(
    [property: JsonPropertyName("handle")] string Handle,
    [property: JsonPropertyName("display_name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplayName);
