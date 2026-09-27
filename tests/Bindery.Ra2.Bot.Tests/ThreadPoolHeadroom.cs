// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Bindery.Ra2.Bot.Tests;

/// <summary>
/// Gives the test process enough pool threads for its blocking waits. Inline scheduling
/// (<c>SchedulerOptions.RunDeterministicStrategistsInline</c>) and several tests wait synchronously for work
/// that itself needs a pool thread; with xUnit running classes in parallel on a two-core CI runner the
/// pool's minimum is two, it grows by about one thread a second, and a wait could time out before its
/// continuation was scheduled (reproduced with the suite pinned to two CPUs).
/// </summary>
internal static class ThreadPoolHeadroom
{
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "Test assembly: sets up the process it runs in.")]
    internal static void Raise()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), completionPorts);
    }
}
