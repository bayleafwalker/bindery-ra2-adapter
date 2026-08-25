// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Bindery.Ra2.Adapter;

/// <summary>
/// The machine that owns one cloned appliance and runs its game process.
/// </summary>
/// <remarks>
/// docs/golden-appliance.md requires the two clients to be separate VMs with
/// distinct Windows and network identity, but the acceptance harness is a
/// single orchestrator. Those are only reconcilable if the orchestrator can
/// drive a client it is not running on. A local host keeps the original
/// single-machine behaviour; a remote host talks to the launch agent inside
/// the other clone.
/// </remarks>
public interface ILiveClientHost
{
    /// <summary>Human-readable origin of this client, recorded in evidence.</summary>
    string Description { get; }

    Task ValidateAsync(LiveClientLaunch launch, CancellationToken cancellationToken);

    Task<string> Sha256Async(string path, CancellationToken cancellationToken);

    /// <summary>Installs the generated SPAWN.INI, preserving any existing file.</summary>
    Task InstallSpawnIniAsync(string workingDirectory, string content, CancellationToken cancellationToken);

    /// <summary>Restores whatever SPAWN.INI was there before, or removes ours.</summary>
    Task RestoreSpawnIniAsync(string workingDirectory, CancellationToken cancellationToken);

    /// <summary>Path the game process will see, which differs off-box.</summary>
    string ResolveSpawnIniPath(string workingDirectory, string orchestratorSpawnIniPath);

    /// <summary>Clears the spawner log so a later read cannot see an old run.</summary>
    Task ResetSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken);

    /// <summary>Reads the spawner log after the run, or null if there is none.</summary>
    Task<string?> ReadSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken);

    Task<int> RunAsync(
        SpawnConfiguration spawn,
        LiveClientLaunch launch,
        string spawnIniPath,
        Func<LifecycleReport, Task> report,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs a client on the machine hosting the orchestrator. This is the original
/// behaviour and stays the default so single-machine runs are unchanged.
/// </summary>
public sealed class LocalLiveClientHost(ISpawnerBoundary spawner) : ILiveClientHost
{
    private readonly ISpawnerBoundary spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
    private string? originalSpawnIniBackupPath;

    public string Description => "local";

    public Task ValidateAsync(LiveClientLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!File.Exists(launch.GameExecutable)) throw new FileNotFoundException("live game executable was not found", launch.GameExecutable);
        if (!Directory.Exists(launch.WorkingDirectory)) throw new DirectoryNotFoundException(launch.WorkingDirectory);
        return Task.CompletedTask;
    }

    public Task<string> Sha256Async(string path, CancellationToken cancellationToken) => Task.FromResult(Hashing.Sha256File(path));

    // On-box the game can read the orchestrator's own generated copy.
    public string ResolveSpawnIniPath(string workingDirectory, string orchestratorSpawnIniPath) => orchestratorSpawnIniPath;

    public async Task InstallSpawnIniAsync(string workingDirectory, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        string runtimePath = Path.Combine(workingDirectory, "SPAWN.INI");
        if (File.Exists(runtimePath))
        {
            originalSpawnIniBackupPath = Path.Combine(Path.GetTempPath(), $"bindery-spawn-backup-{Guid.NewGuid():N}.ini");
            File.Copy(runtimePath, originalSpawnIniBackupPath, overwrite: true);
        }
        await File.WriteAllTextAsync(runtimePath, content, cancellationToken).ConfigureAwait(false);
    }

    public Task RestoreSpawnIniAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        string runtimePath = Path.Combine(workingDirectory, "SPAWN.INI");
        if (originalSpawnIniBackupPath is not null)
        {
            File.Copy(originalSpawnIniBackupPath, runtimePath, overwrite: true);
            File.Delete(originalSpawnIniBackupPath);
            originalSpawnIniBackupPath = null;
        }
        else if (File.Exists(runtimePath))
        {
            File.Delete(runtimePath);
        }
        return Task.CompletedTask;
    }

    public Task ResetSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken)
    {
        string path = Path.Combine(workingDirectory, logName);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<string?> ReadSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken)
    {
        string path = Path.Combine(workingDirectory, logName);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> RunAsync(SpawnConfiguration spawn, LiveClientLaunch launch, string spawnIniPath, Func<LifecycleReport, Task> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ProcessLifecycle lifecycle = new(spawner);
        return lifecycle.RunAsync(spawn, launch.WorkingDirectory, spawnIniPath, report, cancellationToken);
    }
}

/// <summary>
/// Runs a client inside another cloned guest through its launch agent, so the
/// two acceptance clients can be two machines with genuinely distinct Windows
/// and network identity rather than two directories on one box.
/// </summary>
public sealed class RemoteLiveClientHost : ILiveClientHost
{
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient client;
    private readonly TimeSpan pollInterval;

    public RemoteLiveClientHost(HttpClient client, string description, TimeSpan? pollInterval = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public string Description { get; }

    public static HttpClient CreateClient(Uri baseAddress, string token)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        HttpClient client = new() { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<LaunchAgentHealth> CheckAsync(CancellationToken cancellationToken)
    {
        LaunchAgentHealth health = await GetAsync<LaunchAgentHealth>(LaunchAgentProtocol.HealthPath, cancellationToken).ConfigureAwait(false);
        if (health.Version != LaunchAgentProtocol.Version)
            throw new InvalidOperationException($"launch agent at {Description} speaks '{health.Version}', orchestrator speaks '{LaunchAgentProtocol.Version}'");
        return health;
    }

    public async Task ValidateAsync(LiveClientLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        LaunchAgentValidateResponse response = await PostAsync<LaunchAgentValidateRequest, LaunchAgentValidateResponse>(
            LaunchAgentProtocol.ValidatePath,
            new LaunchAgentValidateRequest(launch.GameExecutable, launch.WorkingDirectory),
            cancellationToken).ConfigureAwait(false);
        if (!response.GameExecutableExists) throw new FileNotFoundException($"live game executable was not found on {Description}", launch.GameExecutable);
        if (!response.WorkingDirectoryExists) throw new DirectoryNotFoundException($"{launch.WorkingDirectory} on {Description}");
    }

    public async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        LaunchAgentHashResponse response = await PostAsync<LaunchAgentHashRequest, LaunchAgentHashResponse>(
            LaunchAgentProtocol.HashPath,
            new LaunchAgentHashRequest(path),
            cancellationToken).ConfigureAwait(false);
        return response.Sha256;
    }

    // The orchestrator's evidence copy does not exist on the other guest, so the
    // game process is pointed at the SPAWN.INI the agent just wrote.
    public string ResolveSpawnIniPath(string workingDirectory, string orchestratorSpawnIniPath) =>
        string.Join('\\', workingDirectory.TrimEnd('\\', '/'), "SPAWN.INI");

    public Task InstallSpawnIniAsync(string workingDirectory, string content, CancellationToken cancellationToken) =>
        PostAsync<LaunchAgentSpawnIniRequest, LaunchAgentSpawnIniResponse>(
            LaunchAgentProtocol.SpawnIniPath,
            new LaunchAgentSpawnIniRequest(workingDirectory, content),
            cancellationToken);

    public Task RestoreSpawnIniAsync(string workingDirectory, CancellationToken cancellationToken) =>
        PostAsync<LaunchAgentSpawnIniRequest, LaunchAgentSpawnIniResponse>(
            LaunchAgentProtocol.SpawnIniRestorePath,
            new LaunchAgentSpawnIniRequest(workingDirectory, null),
            cancellationToken);

    public Task ResetSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken) =>
        PostAsync<LaunchAgentLogRequest, LaunchAgentLogResponse>(
            LaunchAgentProtocol.LogResetPath,
            new LaunchAgentLogRequest(workingDirectory, logName),
            cancellationToken);

    public async Task<string?> ReadSpawnerLogAsync(string workingDirectory, string logName, CancellationToken cancellationToken)
    {
        LaunchAgentLogResponse response = await PostAsync<LaunchAgentLogRequest, LaunchAgentLogResponse>(
            LaunchAgentProtocol.LogReadPath,
            new LaunchAgentLogRequest(workingDirectory, logName),
            cancellationToken).ConfigureAwait(false);
        return response.Existed ? response.Content : null;
    }

    public async Task<int> RunAsync(SpawnConfiguration spawn, LiveClientLaunch launch, string spawnIniPath, Func<LifecycleReport, Task> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(report);
        await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Ready)).ConfigureAwait(false);

        LaunchAgentRunResponse started;
        try
        {
            started = await PostAsync<LaunchAgentRunRequest, LaunchAgentRunResponse>(
                LaunchAgentProtocol.RunPath,
                new LaunchAgentRunRequest(
                    launch.GameExecutable,
                    launch.WorkingDirectory,
                    launch.SpawnerExecutable,
                    WindowsSpawnerBoundary.BuildGameArguments(spawn),
                    spawnIniPath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Failed, exception.GetType().Name)).ConfigureAwait(false);
            throw new InvalidOperationException($"launch agent at {Description} did not start the game process", exception);
        }

        await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Started)).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchAgentRunStatus status = await GetAsync<LaunchAgentRunStatus>($"{LaunchAgentProtocol.RunPath}/{started.RunId}", cancellationToken).ConfigureAwait(false);
            if (status.State == LaunchAgentRunStatus.Running)
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (status.State == LaunchAgentRunStatus.Failed || status.ExitCode is null)
            {
                await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Failed, status.Failure)).ConfigureAwait(false);
                throw new InvalidOperationException($"remote client on {Description} failed: {status.Failure ?? "unknown"}");
            }
            int exitCode = status.ExitCode.Value;
            LifecycleKind kind = exitCode == 0 ? LifecycleKind.Exited : LifecycleKind.Failed;
            await report(new LifecycleReport(Guid.NewGuid().ToString(), kind, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return exitCode;
        }
    }

    private async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"launch agent at {Description} returned an empty {typeof(TResponse).Name}");
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(path, body, json, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"launch agent at {Description} returned an empty {typeof(TResponse).Name}");
    }
}
