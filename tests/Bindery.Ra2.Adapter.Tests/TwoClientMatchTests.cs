// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class TwoClientMatchTests
{
    private const string SessionId = "0198c2c3-4d5e-7f70-8123-456789abcdef";
    private const string AllocationId = "0198c2c3-4d5e-7f60-8123-456789abcdef";
    private const string AccountA = "0198c2c3-4d5e-7f72-8123-456789abcdef";
    private const string AccountB = "0198c2c3-4d5e-7f73-8123-456789abcdef";
    private const string ClientA = "0198c2c3-4d5e-7f71-8123-456789abcdef";
    private const string ClientB = "0198c2c3-4d5e-7f74-8123-456789abcdef";

    [Fact]
    public async Task DriverCreatesTwoDistinctEnrollmentsAndAttachesBothNativeRelays()
    {
        byte[] keyA = Enumerable.Repeat((byte)0x11, RelayV1Protocol.TransportKeyBytes).ToArray();
        byte[] keyB = Enumerable.Repeat((byte)0x22, RelayV1Protocol.TransportKeyBytes).ToArray();
        using UdpClient relayListener = new(new IPEndPoint(IPAddress.Loopback, 0));
        int relayPort = ((IPEndPoint)relayListener.Client.LocalEndPoint!).Port;
        using StatefulControlPlaneHandler handler = new(relayPort, keyA, keyB);
        using HttpClient httpClient = new(handler) { BaseAddress = new Uri("https://control-plane.test") };
        BinderyAdapterClient client = new(httpClient);
        TwoClientMatchDriver driver = new(client, httpClient.BaseAddress!);

        MatchClientDefinition first = Definition(AccountA, "account-a-token", "instance-a");
        MatchClientDefinition second = Definition(AccountB, "account-b-token", "instance-b");
        SessionCreationRequest request = new(
            new SessionCompatibility("red-alert-2", "1.0", "sha256:game", "bindery.ra2-adapter", "0.1.0", "vanilla", "sha256:mod", "map-01", "sha256:map"),
            new ParticipantPolicy(2, 2, 0),
            new PlacementIntent(["eu-north"], 100),
            new CapturePolicy(true, true, false));

        await using TwoClientMatch match = await driver.PrepareAsync(
            request,
            first,
            second,
            "session-idempotency",
            "enrollment-a",
            "enrollment-b");

        Assert.Equal(SessionId, match.Session.SessionId);
        Assert.Equal(ClientA, match.First.Enrollment.ClientId);
        Assert.Equal(ClientB, match.Second.Enrollment.ClientId);
        Assert.Equal("instance-a", match.First.Configuration.ClientInstanceId);
        Assert.Equal("instance-b", match.Second.Configuration.ClientInstanceId);
        Assert.Equal(relayPort, match.First.Configuration.RelayPort);
        Assert.Equal(relayPort, match.Second.Configuration.RelayPort);
        Assert.NotEqual(match.First.Relay.LocalEndpoint.Port, match.Second.Relay.LocalEndpoint.Port);
        Assert.Equal(["instance-a", "instance-b"], handler.EnrollmentInstances);

        DateTimeOffset expiresAt = await client.HeartbeatAsync(match.First.Configuration);
        Assert.True(expiresAt > DateTimeOffset.UtcNow);
    }

    private static MatchClientDefinition Definition(string accountId, string accountToken, string instanceId) => new(
        new IdentityCredentials(accountId, accountToken),
        instanceId,
        ClientClass.Player,
        new AdapterIdentity("bindery.ra2-adapter", "0.1.0"),
        new CompatibilityHashes("sha256:game", "sha256:mod", "sha256:map"),
        [new RegionProbe("eu-north", 40)]);

    private sealed class StatefulControlPlaneHandler : HttpMessageHandler
    {
        private readonly int relayPort;
        private readonly string credentialA;
        private readonly string credentialB;
        private readonly List<string> enrollmentInstances = [];

        public StatefulControlPlaneHandler(int relayPort, byte[] keyA, byte[] keyB)
        {
            this.relayPort = relayPort;
            credentialA = ToCredential(keyA);
            credentialB = ToCredential(keyB);
        }

        public IReadOnlyList<string> EnrollmentInstances => enrollmentInstances;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Post && path == "/v1/sessions") return SessionResponse();
            if (request.Method == HttpMethod.Post && path.EndsWith("/enrollments", StringComparison.Ordinal)) return EnrollmentResponse(body);
            if (request.Method == HttpMethod.Post && path == $"/v1/enrollments/{ClientA}:heartbeat") return HeartbeatResponse(ClientA);
            throw new InvalidOperationException($"unexpected {request.Method} {path}");
        }

        private HttpResponseMessage SessionResponse() => JsonResponse(HttpStatusCode.Created, new
        {
            public_session = new
            {
                session_id = SessionId,
                placement = new
                {
                    region = "eu-north",
                    relay_provider_id = "bindery-native",
                    relay_allocation_id = AllocationId,
                    relay_endpoint = $"127.0.0.1:{relayPort}",
                    policy_version = "relay-placement/v1",
                },
            },
            session_join_credential = "join-token",
        });

        private HttpResponseMessage EnrollmentResponse(string body)
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string instance = root.GetProperty("client_instance_id").GetString()!;
            Assert.Equal("player", root.GetProperty("client_class").GetString());
            Assert.Equal("eu-north", root.GetProperty("region_probes")[0].GetProperty("region").GetString());
            enrollmentInstances.Add(instance);
            bool first = string.Equals(instance, "instance-a", StringComparison.Ordinal);
            return JsonResponse(HttpStatusCode.Created, new
            {
                public_enrollment = new { client_id = first ? ClientA : ClientB },
                client_lease_token = first ? "lease-a" : "lease-b",
                transport_credential = first ? credentialA : credentialB,
            });
        }

        private static HttpResponseMessage HeartbeatResponse(string clientId) => JsonResponse(HttpStatusCode.OK, new
        {
            client_id = clientId,
            expires_at = DateTimeOffset.UtcNow.AddMinutes(2),
        });
    }

    private static string ToCredential(byte[] key) => Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
