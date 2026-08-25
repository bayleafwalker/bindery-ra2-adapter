// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter;

public enum ClientClass
{
    Player,
    Observer,
}

public enum RelayProvider
{
    BinderyNative,
    CncNetBaseline,
    CncNetPrivate,
}

public enum LifecycleKind
{
    Ready,
    Started,
    Exited,
    Failed,
    CaptureDegraded,
}

public enum SessionPhase
{
    Created,
    Admitting,
    Ready,
    Running,
    Ended,
    Failed,
    Expired,
    Published,
}

public enum EnrollmentPhase
{
    Issued,
    Registered,
    Ready,
    Active,
    Departed,
    Lost,
    Expired,
}

public sealed record AdapterIdentity(string Id, string Version);

public sealed record CompatibilityHashes(string GameHash, string ModHash, string MapHash);

public sealed record RelayPlacement(
    string Region,
    string RelayProviderId,
    string RelayAllocationId,
    string RelayEndpoint,
    string PolicyVersion,
    string? DecisionSummary = null);

public sealed record AdapterConfiguration(
    Uri ServiceUri,
    string AccountToken,
    string SessionJoinCredential,
    string SessionId,
    string ClientInstanceId,
    string ClientId,
    string ClientLeaseToken,
    ClientClass ClientClass,
    AdapterIdentity Adapter,
    CompatibilityHashes Compatibility,
    RelayProvider RelayProvider,
    string? RelayHost,
    int? RelayPort,
    string? RelayCredential);

public sealed record SpawnConfiguration(
    string GameExecutable,
    string MapId,
    string PlayerName,
    string? RelayHost,
    int? RelayPort,
    string SpawnMode = "multiplayer",
    string? SpawnerExecutable = null,
    IReadOnlyList<string>? SpawnerArguments = null);

public sealed record LifecycleReport(string ReportId, LifecycleKind Kind, string? Reason = null);

public sealed record SessionStatus(
    string SessionId,
    SessionPhase Phase,
    RelayPlacement? Placement,
    IReadOnlyList<EnrollmentStatus> Enrollments);

public sealed record EnrollmentStatus(
    string ClientId,
    string AccountId,
    ClientClass ClientClass,
    EnrollmentPhase Phase,
    string AdapterId,
    string AdapterVersion);

public sealed record LifecycleReportResult(SessionStatus Session, EnrollmentStatus Enrollment);

public sealed record DiscoveredArtifact(string Path, string Sha256, long Bytes);
