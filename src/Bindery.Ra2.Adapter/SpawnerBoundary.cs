// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Bindery.Ra2.Adapter;

public interface ISpawnerBoundary
{
    Task<Process> StartAsync(SpawnConfiguration configuration, string workingDirectory, string spawnIniPath, CancellationToken cancellationToken);
}

public sealed class WindowsSpawnerBoundary : ISpawnerBoundary
{
    public Task<Process> StartAsync(SpawnConfiguration configuration, string workingDirectory, string spawnIniPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("RA2/YR process integration is Windows-only");
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.GameExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(spawnIniPath);
        ProcessStartInfo startInfo = new()
        {
            FileName = configuration.SpawnerExecutable ?? configuration.GameExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        IReadOnlyList<string> arguments = BuildGameArguments(configuration);
        if (configuration.SpawnerExecutable is null)
        {
            foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        }
        else
        {
            foreach (string argument in BuildSyringeArguments(configuration)) startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["BINDERY_SPAWN_INI"] = spawnIniPath;
        Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("game process did not start");
        return Task.FromResult(process);
    }

    internal static IReadOnlyList<string> BuildGameArguments(SpawnConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.GameExecutable);
        IReadOnlyList<string> arguments = configuration.SpawnerArguments ?? ["-SPAWN", "-CD", "-LOG"];
        foreach (string argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument)) throw new ArgumentException("spawner arguments cannot contain blank values", nameof(configuration));
        }
        return arguments;
    }

    internal static IReadOnlyList<string> BuildSyringeArguments(SpawnConfiguration configuration)
    {
        IReadOnlyList<string> arguments = BuildGameArguments(configuration);
        return [configuration.GameExecutable, $"--args={string.Join(' ', arguments)}"];
    }
}
