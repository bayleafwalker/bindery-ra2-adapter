// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class ControlPlaneClientTests
{
    private const string SessionId = "0198c2c3-4d5e-7f70-8123-456789abcdef";
    private const string ClientId = "0198c2c3-4d5e-7f71-8123-456789abcdef";
    private const string AccountId = "0198c2c3-4d5e-7f72-8123-456789abcdef";
    private const string AllocationId = "0198c2c3-4d5e-7f60-8123-456789abcdef";

    [Fact]
    public async Task CreateSessionUsesTheFrozenSnakeCaseV1Request()
    {
        using RecordingHandler handler = new((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/sessions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer account-token", request.Headers.Authorization!.ToString());
            Assert.Equal("session-idempotency", request.Headers.GetValues("Idempotency-Key").Single());
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            Assert.Equal("red-alert-2", root.GetProperty("compatibility").GetProperty("game_family").GetString());
            Assert.Equal(2, root.GetProperty("participant_policy").GetProperty("required_players").GetInt32());
            Assert.Equal("eu-north", root.GetProperty("placement").GetProperty("allowed_regions")[0].GetString());
            Assert.True(root.GetProperty("capture").GetProperty("semantic_events").GetBoolean());
            return JsonResponse($"{{\"public_session\":{{\"session_id\":\"{SessionId}\",\"placement\":{{\"region\":\"eu-north\",\"relay_provider_id\":\"bindery-native\",\"relay_allocation_id\":\"{AllocationId}\",\"relay_endpoint\":\"127.0.0.1:40000\",\"policy_version\":\"relay-placement/v1\"}}}},\"session_join_credential\":\"join-token\"}}");
        });
        using HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://control-plane.test") };
        BinderyAdapterClient client = new(httpClient);

        SessionCredentials credentials = await client.CreateSessionAsync(
            "account-token",
            new SessionCreationRequest(
                new SessionCompatibility("red-alert-2", "1.0", "sha256:game", "bindery.ra2-adapter", "0.1.0", "vanilla", "sha256:mod", "map-01", "sha256:map"),
                new ParticipantPolicy(2, 2, 0),
                new PlacementIntent(["eu-north"], 100),
                new CapturePolicy(true, true, false)),
            "session-idempotency",
            CancellationToken.None);

        Assert.Equal(SessionId, credentials.SessionId);
        Assert.Equal("join-token", credentials.SessionJoinCredential);
        Assert.Equal("bindery-native", credentials.Placement!.RelayProviderId);
    }

    [Fact]
    public async Task ReportUsesTheControlPlaneStringLifecycleKind()
    {
        using RecordingHandler handler = new((request, body) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/v1/enrollments/{ControlPlaneClientTests.ClientId}/reports", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer lease-token", request.Headers.Authorization!.ToString());
            using JsonDocument document = JsonDocument.Parse(body);
            Assert.Equal("report-1", document.RootElement.GetProperty("report_id").GetString());
            Assert.Equal("capture_degraded", document.RootElement.GetProperty("kind").GetString());
            Assert.Equal("fixture gap", document.RootElement.GetProperty("reason").GetString());
            return JsonResponse(HttpStatusCode.OK, LifecycleResponseJson("running", "active"));
        });
        using HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://control-plane.test") };
        BinderyAdapterClient client = new(httpClient);
        AdapterConfiguration configuration = new(
            new Uri("https://control-plane.test"),
            "account-token",
            "join-token",
            SessionId,
            "machine-a",
            ClientId,
            "lease-token",
            ClientClass.Player,
            new AdapterIdentity("bindery.ra2-adapter", "0.1.0"),
            new CompatibilityHashes("sha256:game", "sha256:mod", "sha256:map"),
            RelayProvider.CncNetBaseline,
            null,
            null,
            null);

        LifecycleReportResult result = await client.ReportAsync(configuration, new LifecycleReport("report-1", LifecycleKind.CaptureDegraded, "fixture gap"), CancellationToken.None);

        Assert.Equal(SessionPhase.Running, result.Session.Phase);
        Assert.Equal(ClientId, result.Enrollment.ClientId);
        Assert.Equal(EnrollmentPhase.Active, result.Enrollment.Phase);
    }

    [Fact]
    public async Task KnownIdReadsExposeSessionAndEnrollmentState()
    {
        using RecordingHandler handler = new((request, body) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return request.RequestUri!.AbsolutePath switch
            {
                $"/v1/sessions/{SessionId}" => JsonResponse(HttpStatusCode.OK, $"{{\"session_id\":\"{SessionId}\",\"phase\":\"ready\",\"placement\":{{\"region\":\"eu-north\",\"relay_provider_id\":\"bindery-native\",\"relay_allocation_id\":\"{AllocationId}\",\"relay_endpoint\":\"127.0.0.1:40000\",\"policy_version\":\"relay-placement/v1\"}},\"enrollments\":[{EnrollmentJson("registered")}]}}"),
                $"/v1/enrollments/{ClientId}" => JsonResponse(HttpStatusCode.OK, EnrollmentJson("ready")),
                _ => throw new Xunit.Sdk.XunitException($"unexpected path {request.RequestUri.AbsolutePath}"),
            };
        });
        using HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://control-plane.test") };
        BinderyAdapterClient client = new(httpClient);

        SessionStatus session = await client.GetSessionAsync(SessionId);
        EnrollmentStatus enrollment = await client.GetEnrollmentAsync(ClientId);

        Assert.Equal(SessionPhase.Ready, session.Phase);
        Assert.Equal("bindery-native", session.Placement!.RelayProviderId);
        Assert.Equal(ClientClass.Player, session.Enrollments.Single().ClientClass);
        Assert.Equal(EnrollmentPhase.Registered, session.Enrollments.Single().Phase);
        Assert.Equal(EnrollmentPhase.Ready, enrollment.Phase);
    }

    private static string LifecycleResponseJson(string sessionPhase, string enrollmentPhase) => $"{{\"public_session\":{{\"session_id\":\"{SessionId}\",\"phase\":\"{sessionPhase}\",\"enrollments\":[{EnrollmentJson(enrollmentPhase)}]}},\"public_enrollment\":{EnrollmentJson(enrollmentPhase)}}}";

    private static string EnrollmentJson(string phase) => $"{{\"client_id\":\"{ClientId}\",\"account_id\":\"{AccountId}\",\"client_class\":\"player\",\"phase\":\"{phase}\",\"adapter_id\":\"bindery.ra2-adapter\",\"adapter_version\":\"0.1.0\"}}";

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task AReplayedSessionCreateNamesTheReusedIdempotencyKey()
    {
        // The join credential is one-time: reusing an idempotency key replays
        // the public session without it, and the old failure surfaced much
        // later as an empty-string argument error during enrollment.
        using RecordingHandler handler = new((_, _) =>
            JsonResponse($"{{\"public_session\":{{\"session_id\":\"{SessionId}\",\"placement\":{{\"region\":\"eu-north\",\"relay_provider_id\":\"cncnet-private\",\"relay_allocation_id\":\"{AllocationId}\",\"relay_endpoint\":\"192.168.122.1:50001\",\"policy_version\":\"cncnet-private-lab-v1\"}}}},\"session_join_credential\":\"\"}}"));
        using HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://control-plane.test") };
        BinderyAdapterClient client = new(httpClient);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateSessionAsync(
            "account-token",
            new SessionCreationRequest(
                new SessionCompatibility("yuris-revenge", "cncnet-ra2-mode", "sha256:game", "bindery.ra2.yr-cncnet", "0.2.0", "cncnet-ra2-mode", "sha256:mod", "map-01", "sha256:map"),
                new ParticipantPolicy(2, 2, 0),
                new PlacementIntent(["eu-north"], 100),
                new CapturePolicy(true, true, false)),
            "reused-key",
            CancellationToken.None));

        Assert.Contains("idempotency key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.Created)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }
}
