// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter;

/// <summary>
/// The reproducible live-lab profile. These values describe the compatibility
/// target; placement still comes from the Bindery coordinator.
/// </summary>
public static class Ra2LabProfile
{
    public const string GameFamily = "yuris-revenge";
    public const string GameVersion = "cncnet-ra2-mode";
    public const string ModId = "cncnet-ra2-mode";
    public const string AdapterId = "bindery.ra2.yr-cncnet";
    public const string AdapterVersion = "0.2.0";
    public const string TransportProviderId = "cncnet-private";
    public const string GoldenApplianceSchemaVersion = "bindery.ra2.golden-appliance/v2";
    public const string LiveEvidenceSchemaVersion = "bindery.ra2.live-acceptance/v2";
    public const string TelemetryProtocol = "ra2yrcpp-protobuf-tcp";
    public const int DefaultTelemetryPort = 14521;
}

public sealed record GoldenApplianceArtifact(string RelativePath, string Sha256, long Bytes);

public sealed record GoldenApplianceProvenance(
    string SourceRepository,
    string SourceRef,
    string SourceCommit,
    string ReleaseAsset,
    string ArtifactRelativePath,
    string ArtifactSha256,
    long ArtifactBytes,
    string SelectionReason);

public sealed record GoldenApplianceManifest(
    string SchemaVersion,
    string ApplianceId,
    string SourceInstallPath,
    string SourceKind,
    string GameFamily,
    string GameVersion,
    string TransportProfile,
    IReadOnlyList<GoldenApplianceArtifact> Artifacts,
    string ClonePolicy,
    GoldenApplianceProvenance SpawnerProvenance);
