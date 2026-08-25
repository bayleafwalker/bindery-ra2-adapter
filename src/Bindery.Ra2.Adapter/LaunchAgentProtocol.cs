// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter;

/// <summary>
/// Wire contract between the acceptance orchestrator and the launch agent
/// running inside the other cloned guest. Deliberately small: the agent may
/// hash a file, install the generated SPAWN.INI, start the pinned spawner, and
/// report the process result. It cannot be asked to run arbitrary commands.
/// </summary>
public static class LaunchAgentProtocol
{
    public const string Version = "bindery.ra2.launch-agent/v3";
    public const string HealthPath = "/health";
    public const string ValidatePath = "/validate";
    public const string HashPath = "/hash";
    public const string SpawnIniPath = "/spawn-ini";
    public const string SpawnIniRestorePath = "/spawn-ini/restore";
    public const string RunPath = "/run";
    public const string LogResetPath = "/log/reset";
    public const string LogReadPath = "/log/read";

    /// <summary>
    /// Diagnostic shell. Present only when the agent is started with
    /// --allow-exec, because it is a remote shell inside the specimen and an
    /// acceptance run has no business exposing one.
    /// </summary>
    public const string ExecPath = "/exec";
}

public sealed record LaunchAgentHealth(string Agent, string Version, string Hostname, string? ClientInstanceId);

public sealed record LaunchAgentValidateRequest(string GameExecutable, string WorkingDirectory);

public sealed record LaunchAgentValidateResponse(bool GameExecutableExists, bool WorkingDirectoryExists);

public sealed record LaunchAgentHashRequest(string Path);

public sealed record LaunchAgentHashResponse(string Sha256);

public sealed record LaunchAgentSpawnIniRequest(string WorkingDirectory, string? Content);

public sealed record LaunchAgentSpawnIniResponse(bool ExistingFilePreserved);

public sealed record LaunchAgentRunRequest(
    string GameExecutable,
    string WorkingDirectory,
    string? SpawnerExecutable,
    IReadOnlyList<string> Arguments,
    string SpawnIniPath);

public sealed record LaunchAgentRunResponse(string RunId);

public sealed record LaunchAgentLogRequest(string WorkingDirectory, string LogName, int MaxBytes = 262144);

public sealed record LaunchAgentLogResponse(string? Content, bool Existed);

public sealed record LaunchAgentExecRequest(string Command, string Shell = "powershell", int TimeoutSeconds = 120);

public sealed record LaunchAgentExecResponse(int ExitCode, string Stdout, string Stderr, bool TimedOut);

public sealed record LaunchAgentRunStatus(string State, int? ExitCode, string? Failure)
{
    public const string Running = "running";
    public const string Exited = "exited";
    public const string Failed = "failed";
}
