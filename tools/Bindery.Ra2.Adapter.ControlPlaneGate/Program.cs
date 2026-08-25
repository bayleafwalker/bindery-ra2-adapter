// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: Bindery.Ra2.Adapter.ControlPlaneGate <golden-manifest.json> [service-uri]");
    return 2;
}

try
{
    string manifestPath = Path.GetFullPath(args[0]);
    Uri serviceUri = new(args.Length == 2 ? args[1] : "http://127.0.0.1:18080", UriKind.Absolute);
    GoldenApplianceManifest manifest = JsonSerializer.Deserialize<GoldenApplianceManifest>(
        await File.ReadAllTextAsync(manifestPath),
        new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
        ?? throw new InvalidOperationException("golden appliance manifest was empty");
    if (manifest.SchemaVersion != Ra2LabProfile.GoldenApplianceSchemaVersion)
        throw new InvalidOperationException("golden appliance manifest schema is unsupported");
    if (manifest.ApplianceId != "ra2-yr-cncnet-v0.1")
        throw new InvalidOperationException("unexpected golden appliance id");
    GoldenApplianceArtifact game = manifest.Artifacts.FirstOrDefault(static artifact =>
        string.Equals(artifact.RelativePath, "gamemd.exe", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("golden appliance manifest does not contain gamemd.exe");
    GoldenApplianceArtifact spawner = manifest.Artifacts.FirstOrDefault(static artifact =>
        string.Equals(artifact.RelativePath, "CnCNet-Spawner.dll", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("golden appliance manifest does not contain CnCNet-Spawner.dll");
    if (manifest.SpawnerProvenance.ArtifactSha256 != spawner.Sha256 || manifest.SpawnerProvenance.ArtifactBytes != spawner.Bytes)
        throw new InvalidOperationException("golden appliance spawner provenance does not match the selected package artifact");

    using HttpClient httpClient = new() { BaseAddress = serviceUri };
    BinderyAdapterClient controlPlane = new(httpClient);
    string suffix = Guid.NewGuid().ToString("N")[..12];
    IdentityCredentials firstIdentity = await controlPlane.CreateIdentityAsync(
        $"ra2-gate-a-{suffix}", "Bindery RA2 gate A", $"ra2-gate-identity-a-{suffix}", CancellationToken.None);
    IdentityCredentials secondIdentity = await controlPlane.CreateIdentityAsync(
        $"ra2-gate-b-{suffix}", "Bindery RA2 gate B", $"ra2-gate-identity-b-{suffix}", CancellationToken.None);

    string modHash = "sha256:" + new string('0', 64);
    string mapHash = "sha256:" + new string('1', 64);
    SessionCreationRequest session = new(
        new SessionCompatibility(
            Ra2LabProfile.GameFamily,
            Ra2LabProfile.GameVersion,
            game.Sha256,
            Ra2LabProfile.AdapterId,
            Ra2LabProfile.AdapterVersion,
            Ra2LabProfile.ModId,
            modHash,
            "official:gate-map",
            mapHash),
        new ParticipantPolicy(2, 2, 0),
        new PlacementIntent(["eu-north"], 100),
        new CapturePolicy(true, true, false));

    MatchClientDefinition first = Client(firstIdentity, "client-a", game.Sha256, modHash, mapHash);
    MatchClientDefinition second = Client(secondIdentity, "client-b", game.Sha256, modHash, mapHash);
    CncNetPrivateMatchDriver driver = new(controlPlane, serviceUri);
    await using PreparedLiveMatch prepared = await driver.PrepareLiveAsync(
        session,
        first,
        second,
        $"ra2-gate-session-{suffix}",
        $"ra2-gate-enrollment-a-{suffix}",
        $"ra2-gate-enrollment-b-{suffix}");

    if (prepared.First.Definition.Identity.AccountId == prepared.Second.Definition.Identity.AccountId)
        throw new InvalidOperationException("control plane returned duplicate account identities");
    if (prepared.First.Enrollment.ClientId == prepared.Second.Enrollment.ClientId)
        throw new InvalidOperationException("control plane returned duplicate enrollment identities");
    if (prepared.First.Placement.RelayProviderId != Ra2LabProfile.TransportProviderId ||
        prepared.Second.Placement.RelayProviderId != Ra2LabProfile.TransportProviderId)
        throw new InvalidOperationException("control plane did not select the private CnCNet provider");
    if (prepared.First.Placement.RelayEndpoint != prepared.Second.Placement.RelayEndpoint)
        throw new InvalidOperationException("two clients received different private relay endpoints");
    SessionStatus status = await controlPlane.GetSessionAsync(prepared.Session.SessionId);
    if (status.Phase != SessionPhase.Admitting || status.Enrollments.Count != 2)
        throw new InvalidOperationException($"coordinator did not admit the two-client session (phase={status.Phase}, enrollments={status.Enrollments.Count})");
    _ = await controlPlane.HeartbeatAsync(prepared.First.Configuration);

    Console.WriteLine($"service_uri={serviceUri}");
    Console.WriteLine($"golden_appliance={manifest.ApplianceId}");
    Console.WriteLine($"session_id={prepared.Session.SessionId}");
    Console.WriteLine($"client_a_id={prepared.First.Enrollment.ClientId}");
    Console.WriteLine($"client_b_id={prepared.Second.Enrollment.ClientId}");
    Console.WriteLine($"relay_provider={prepared.First.Placement.RelayProviderId}");
    Console.WriteLine($"relay_endpoint={prepared.First.Placement.RelayEndpoint}");
    Console.WriteLine($"session_phase={status.Phase.ToString().ToLowerInvariant()}");
    Console.WriteLine($"enrollment_count={status.Enrollments.Count}");
    Console.WriteLine("heartbeat=passed");
    Console.WriteLine("control_plane_gate=passed");
    Console.WriteLine("live_game_gate=not-run (requires two Windows clients and human observation)");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"control-plane gate failed: {exception.Message}");
    return 1;
}

static MatchClientDefinition Client(IdentityCredentials identity, string instanceId, string gameHash, string modHash, string mapHash) => new(
    identity,
    instanceId,
    ClientClass.Player,
    new AdapterIdentity(Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion),
    new CompatibilityHashes(gameHash, modHash, mapHash),
    [new RegionProbe("eu-north", 20)]);
