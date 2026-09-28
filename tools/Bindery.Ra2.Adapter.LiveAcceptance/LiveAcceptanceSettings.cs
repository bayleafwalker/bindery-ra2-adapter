// SPDX-License-Identifier: GPL-3.0-or-later
// Shared by the live-acceptance and channel tools, which play the same match.
using System.Text.Json;
using Bindery.Ra2.Adapter;

/// <summary>Builds the owner of one client: this machine, or a cloned guest's launch agent.</summary>
internal static class LiveHosts
{
    public static async Task<(ILiveClientHost Host, HttpClient? Agent)> BuildAsync(LiveHostSettings? host, ISpawnerBoundary spawner)
    {
        if (host is null || string.IsNullOrWhiteSpace(host.Uri)) return (new LocalLiveClientHost(spawner), null);
        string token = host.Token;
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(host.TokenFile))
            token = (await File.ReadAllTextAsync(host.TokenFile)).Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        Uri uri = new(host.Uri, UriKind.Absolute);
        HttpClient client = RemoteLiveClientHost.CreateClient(uri, token);
        RemoteLiveClientHost remote = new(client, uri.Authority);
        LaunchAgentHealth health = await remote.CheckAsync(CancellationToken.None);
        Console.WriteLine($"launch agent {uri.Authority}: host={health.Hostname} client_instance={health.ClientInstanceId ?? "(unset)"}");
        return (remote, client);
    }
}

internal sealed class LiveHostSettings
{
    public string Uri { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public string TokenFile { get; init; } = string.Empty;
}

internal sealed class LiveAcceptanceSettings
{
    /// <summary>Owner of the first client; omit for this machine.</summary>
    public LiveHostSettings? FirstHost { get; init; }

    /// <summary>Owner of the second client; omit for this machine.</summary>
    public LiveHostSettings? SecondHost { get; init; }

    /// <summary>
    /// An optional spectator client: its own identity, instance, launch and
    /// host. Omit all four for the proven two-client match.
    /// </summary>
    public LiveIdentitySettings? ObserverIdentity { get; init; }
    public string ObserverClientInstanceId { get; init; } = string.Empty;
    public LiveClientLaunch? ObserverLaunch { get; init; }
    public LiveHostSettings? ObserverHost { get; init; }
    public string ObserverEnrollmentIdempotencyKey { get; set; } = string.Empty;

    public bool HasObserver => ObserverLaunch is not null;

    public string ServiceUri { get; init; } = string.Empty;
    public LiveIdentitySettings FirstIdentity { get; init; } = new();
    public LiveIdentitySettings SecondIdentity { get; init; } = new();
    public string AdapterId { get; init; } = Ra2LabProfile.AdapterId;
    public string AdapterVersion { get; init; } = Ra2LabProfile.AdapterVersion;
    public string GameFamily { get; init; } = Ra2LabProfile.GameFamily;
    public string GameVersion { get; init; } = Ra2LabProfile.GameVersion;
    public string ModId { get; init; } = Ra2LabProfile.ModId;
    public string TransportProvider { get; init; } = Ra2LabProfile.TransportProviderId;
    public string GoldenApplianceId { get; init; } = "ra2-yr-cncnet-v0.2";
    public string GoldenManifestPath { get; init; } = string.Empty;
    public string TelemetryEndpoint { get; init; } = $"127.0.0.1:{Ra2LabProfile.DefaultTelemetryPort}";
    public string TelemetryProtocol { get; init; } = Ra2LabProfile.TelemetryProtocol;
    public string MapId { get; init; } = string.Empty;
    public string GameHash { get; init; } = string.Empty;
    public string ModHash { get; init; } = string.Empty;
    public string MapHash { get; init; } = string.Empty;
    public string FirstClientInstanceId { get; init; } = string.Empty;
    public string SecondClientInstanceId { get; init; } = string.Empty;
    public LiveClientLaunch FirstLaunch { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
    public LiveClientLaunch SecondLaunch { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty);
    public string EvidenceDirectory { get; init; } = string.Empty;
    public string SessionIdempotencyKey { get; set; } = string.Empty;
    public string FirstEnrollmentIdempotencyKey { get; set; } = string.Empty;
    public string SecondEnrollmentIdempotencyKey { get; set; } = string.Empty;
    /// <summary>Tunnel web endpoint that allocates per-player ports; defaults to one port below the placed relay.</summary>
    public string TunnelV2Uri { get; init; } = string.Empty;

    /// <summary>Match rules; omit for plain CnCNet defaults.</summary>
    public SpawnGameOptions? GameOptions { get; init; }

    /// <summary>
    /// Computer-controlled houses. Their seats follow the human seats, so two
    /// clients plus two AI need a four-seat map. Handicap 2 is aggressive; a shared positive Team allies them.
    /// </summary>
    public SpawnAiParticipant[]? AiPlayers { get; init; }

    public string Region { get; init; } = "eu-north";
    public int RegionRttMilliseconds { get; init; } = 40;
    public int LatencyP95Milliseconds { get; init; } = 100;

    /// <summary>
    /// Fills any blank idempotency key with a fresh one. Leaving them blank in
    /// the settings file is the normal case: each run is a new match. Setting
    /// them explicitly is for deliberately retrying one specific run.
    /// </summary>
    public LiveAcceptanceSettings WithFreshIdempotencyKeys()
    {
        LiveAcceptanceSettings copy = (LiveAcceptanceSettings)MemberwiseClone();
        if (string.IsNullOrWhiteSpace(copy.SessionIdempotencyKey)) copy.SessionIdempotencyKey = Guid.NewGuid().ToString("D");
        if (string.IsNullOrWhiteSpace(copy.FirstEnrollmentIdempotencyKey)) copy.FirstEnrollmentIdempotencyKey = Guid.NewGuid().ToString("D");
        if (string.IsNullOrWhiteSpace(copy.SecondEnrollmentIdempotencyKey)) copy.SecondEnrollmentIdempotencyKey = Guid.NewGuid().ToString("D");
        if (string.IsNullOrWhiteSpace(copy.ObserverEnrollmentIdempotencyKey)) copy.ObserverEnrollmentIdempotencyKey = Guid.NewGuid().ToString("D");
        return copy;
    }

    /// <summary>
    /// Fresh keys regardless of the file: every channel match is a new match,
    /// and a replayed session create would return no join credential.
    /// </summary>
    public LiveAcceptanceSettings WithNewIdempotencyKeys()
    {
        LiveAcceptanceSettings copy = (LiveAcceptanceSettings)MemberwiseClone();
        copy.SessionIdempotencyKey = string.Empty;
        copy.FirstEnrollmentIdempotencyKey = string.Empty;
        copy.SecondEnrollmentIdempotencyKey = string.Empty;
        copy.ObserverEnrollmentIdempotencyKey = string.Empty;
        return copy.WithFreshIdempotencyKeys();
    }

    /// <summary>The live request for one match. Call after the idempotency keys are filled.</summary>
    public LiveAcceptanceRequest ToRequest(string evidenceDirectory, int? seed = null)
    {
        LiveObserverClient? observer = HasObserver
            ? new LiveObserverClient(
                ToClientDefinition(ObserverIdentity ?? throw new ArgumentException("an observer needs observerIdentity"), ObserverClientInstanceId, ClientClass.Observer),
                ObserverLaunch!,
                ObserverEnrollmentIdempotencyKey)
            : null;
        return new LiveAcceptanceRequest(
            ToSessionRequest(),
            ToClientDefinition(FirstIdentity, FirstClientInstanceId),
            ToClientDefinition(SecondIdentity, SecondClientInstanceId),
            FirstLaunch,
            SecondLaunch,
            SessionIdempotencyKey,
            FirstEnrollmentIdempotencyKey,
            SecondEnrollmentIdempotencyKey,
            evidenceDirectory,
            GoldenApplianceId,
            TelemetryEndpoint,
            TelemetryProtocol,
            string.IsNullOrWhiteSpace(TunnelV2Uri) ? null : new Uri(TunnelV2Uri, UriKind.Absolute),
            GameOptions,
            AiPlayers,
            seed,
            observer);
    }

    public MatchClientDefinition ToClientDefinition(LiveIdentitySettings identity, string instanceId, ClientClass clientClass = ClientClass.Player) => new(
        new IdentityCredentials(identity.AccountId, identity.AccountToken),
        instanceId,
        clientClass,
        new AdapterIdentity(AdapterId, AdapterVersion),
        new CompatibilityHashes(GameHash, ModHash, MapHash),
        [new RegionProbe(Region, RegionRttMilliseconds)]);

    public SessionCreationRequest ToSessionRequest() => new(
        new SessionCompatibility(GameFamily, GameVersion, GameHash, AdapterId, AdapterVersion, ModId, ModHash, MapId, MapHash),
        // The session must admit the spectator, or its enrollment is refused.
        new ParticipantPolicy(2, 2, HasObserver ? 1 : 0),
        new PlacementIntent([Region], LatencyP95Milliseconds),
        new CapturePolicy(true, true, HasObserver));

    public async Task ValidateAsync(ILiveClientHost firstHost, ILiveClientHost secondHost, ILiveClientHost? observerHost = null)
    {
        ArgumentNullException.ThrowIfNull(firstHost);
        ArgumentNullException.ThrowIfNull(secondHost);
        ArgumentException.ThrowIfNullOrWhiteSpace(ServiceUri);
        if (AdapterId != Ra2LabProfile.AdapterId) throw new ArgumentException($"live acceptance requires adapter id '{Ra2LabProfile.AdapterId}'");
        if (GameFamily != Ra2LabProfile.GameFamily) throw new ArgumentException($"live acceptance requires game family '{Ra2LabProfile.GameFamily}'");
        if (GameVersion != Ra2LabProfile.GameVersion) throw new ArgumentException($"live acceptance requires game version '{Ra2LabProfile.GameVersion}'");
        if (ModId != Ra2LabProfile.ModId) throw new ArgumentException($"live acceptance requires mod id '{Ra2LabProfile.ModId}'");
        if (TransportProvider != Ra2LabProfile.TransportProviderId && TransportProvider != "bindery-native") throw new ArgumentException("transport provider must be cncnet-private or bindery-native");
        ArgumentException.ThrowIfNullOrWhiteSpace(FirstIdentity.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(FirstIdentity.AccountToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(SecondIdentity.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SecondIdentity.AccountToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(MapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(GameHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(ModHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(MapHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(FirstClientInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SecondClientInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(EvidenceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(GoldenApplianceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(GoldenManifestPath);
        if (!File.Exists(GoldenManifestPath)) throw new FileNotFoundException("golden appliance manifest was not found", GoldenManifestPath);
        GoldenApplianceManifest manifest = JsonSerializer.Deserialize<GoldenApplianceManifest>(
            File.ReadAllText(GoldenManifestPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
            ?? throw new InvalidOperationException("golden appliance manifest was empty");
        if (manifest.SchemaVersion != Ra2LabProfile.GoldenApplianceSchemaVersion) throw new ArgumentException("golden appliance manifest schema is unsupported");
        if (manifest.ApplianceId != GoldenApplianceId) throw new ArgumentException("golden appliance id does not match the manifest");
        if (manifest.TransportProfile != Ra2LabProfile.TransportProviderId) throw new ArgumentException("golden appliance is not pinned to the private CnCNet profile");
        GoldenApplianceArtifact? gameArtifact = manifest.Artifacts.FirstOrDefault(static artifact => string.Equals(artifact.RelativePath, "gamemd.exe", StringComparison.OrdinalIgnoreCase));
        if (gameArtifact is null) throw new ArgumentException("golden appliance manifest does not contain gamemd.exe");
        if (!string.Equals(GameHash, gameArtifact.Sha256, StringComparison.Ordinal)) throw new ArgumentException("gameHash does not match the golden gamemd.exe hash");
        GoldenApplianceArtifact? spawnerArtifact = manifest.Artifacts.FirstOrDefault(static artifact => string.Equals(artifact.RelativePath, "CnCNet-Spawner.dll", StringComparison.OrdinalIgnoreCase));
        if (spawnerArtifact is null || manifest.SpawnerProvenance.ArtifactRelativePath != spawnerArtifact.RelativePath || manifest.SpawnerProvenance.ArtifactSha256 != spawnerArtifact.Sha256 || manifest.SpawnerProvenance.ArtifactBytes != spawnerArtifact.Bytes)
            throw new ArgumentException("golden appliance spawner provenance does not match the selected package artifact");
        // Hash each executable on the machine that will actually run it --
        // hashing a local copy would prove nothing about the other guest.
        string firstGameHash = await firstHost.Sha256Async(FirstLaunch.GameExecutable, CancellationToken.None);
        string secondGameHash = await secondHost.Sha256Async(SecondLaunch.GameExecutable, CancellationToken.None);
        if (!string.Equals(firstGameHash, gameArtifact.Sha256, StringComparison.Ordinal) || !string.Equals(secondGameHash, gameArtifact.Sha256, StringComparison.Ordinal))
            throw new ArgumentException("both live game executables must match the golden gamemd.exe hash");
        _ = Ra2YrcppEndpoint.Parse(TelemetryEndpoint);
        // Idempotency keys are filled in by WithFreshIdempotencyKeys before the
        // run; a blank one here is normal and not an error.
        if (string.Equals(FirstIdentity.AccountId, SecondIdentity.AccountId, StringComparison.Ordinal)) throw new ArgumentException("live clients must use distinct account ids");
        if (string.Equals(FirstClientInstanceId, SecondClientInstanceId, StringComparison.Ordinal)) throw new ArgumentException("live clients must use distinct client instance ids");
        if (HasObserver)
        {
            ArgumentNullException.ThrowIfNull(observerHost);
            if (ObserverIdentity is null) throw new ArgumentException("an observer needs observerIdentity");
            ArgumentException.ThrowIfNullOrWhiteSpace(ObserverIdentity.AccountId);
            ArgumentException.ThrowIfNullOrWhiteSpace(ObserverIdentity.AccountToken);
            ArgumentException.ThrowIfNullOrWhiteSpace(ObserverClientInstanceId);
            if (!ObserverLaunch!.IsSpectator) throw new ArgumentException("observerLaunch must set isSpectator");
            string observerGameHash = await observerHost.Sha256Async(ObserverLaunch.GameExecutable, CancellationToken.None);
            if (!string.Equals(observerGameHash, gameArtifact.Sha256, StringComparison.Ordinal))
                throw new ArgumentException("the observer game executable must match the golden gamemd.exe hash");
        }
    }
}

internal sealed class LiveIdentitySettings
{
    public string AccountId { get; init; } = string.Empty;
    public string AccountToken { get; init; } = string.Empty;
}
