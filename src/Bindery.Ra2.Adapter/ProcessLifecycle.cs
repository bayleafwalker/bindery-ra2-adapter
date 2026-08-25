// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Bindery.Ra2.Adapter;

public sealed class ProcessLifecycle(ISpawnerBoundary spawner)
{
    public async Task<int> RunAsync(SpawnConfiguration configuration, string workingDirectory, string spawnIniPath, Func<LifecycleReport, Task> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Ready)).ConfigureAwait(false);
        Process process;
        try
        {
            process = await spawner.StartAsync(configuration, workingDirectory, spawnIniPath, cancellationToken).ConfigureAwait(false);
            await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Started)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            await report(new LifecycleReport(Guid.NewGuid().ToString(), LifecycleKind.Failed, exception.GetType().Name)).ConfigureAwait(false);
            throw;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            LifecycleKind kind = process.ExitCode == 0 ? LifecycleKind.Exited : LifecycleKind.Failed;
            await report(new LifecycleReport(Guid.NewGuid().ToString(), kind, process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception cleanupException) when (cleanupException is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                // The cancellation is still propagated; cleanup is best effort
                // because platform-specific process handles may already be gone.
            }
            throw;
        }
        finally
        {
            process.Dispose();
        }
    }
}
