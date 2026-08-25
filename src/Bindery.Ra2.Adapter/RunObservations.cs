// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bindery.Ra2.Adapter;

/// <summary>
/// Something worth recording about one client's run.
/// </summary>
/// <remarks>
/// These are observations, not verdicts. Whether a run is acceptable is
/// decided by the qualification preflight from the whole picture; this type
/// only reports what was seen.
/// </remarks>
public sealed record RunObservation(string Kind, string Detail, bool Notable)
{
    /// <summary>A C++ exception the debugger saw. Usually first-chance and handled.</summary>
    public const string SpawnerException = "spawner_exception";

    /// <summary>The game wrote a synchronisation dump: the simulations diverged.</summary>
    public const string Desync = "desync";

    /// <summary>The hosted game's own exit code, as recorded by the debugger.</summary>
    public const string HostedExitCode = "hosted_exit_code";
}

/// <summary>
/// Reads a spawner debugger log for events worth recording.
/// </summary>
/// <remarks>
/// Syringe is a debugger: it logs first-chance exceptions, meaning ones the
/// game throws and handles itself, and RA2 with Ares does that routinely
/// during an ordinary match. An exception line is therefore an event, not a
/// failure. Likewise the hosted exit code is just a number -- Yuri's Revenge
/// exits with 3 when the player quits from the score screen.
///
/// Nothing here decides pass or fail. Earlier versions did, and marked every
/// completed match as a crash three different ways.
/// </remarks>
public static class SpawnerLogObservations
{
    private static readonly string[] exceptionMarkers = ["Exception (Code:", "Access violation"];

    public static IReadOnlyList<RunObservation> Read(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return [];
        List<RunObservation> observations = [];

        int exceptions = 0;
        string? firstException = null;
        string? exitCode = null;
        foreach (string line in log.Split('\n'))
        {
            foreach (string marker in exceptionMarkers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    exceptions++;
                    firstException ??= Trim(line);
                    break;
                }
            }
            int at = line.IndexOf("Done with exit code", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) exitCode = Trim(line[at..]);
        }

        if (exceptions > 0)
        {
            observations.Add(new RunObservation(
                RunObservation.SpawnerException,
                $"{exceptions.ToString(CultureInfo.InvariantCulture)} debugger exception line(s); first: {firstException}",
                Notable: false));
        }
        if (exitCode is not null) observations.Add(new RunObservation(RunObservation.HostedExitCode, exitCode, Notable: false));
        return observations;
    }

    private static string Trim(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length > 240 ? trimmed[..240] : trimmed;
    }
}
