// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bindery.Ra2.Adapter;

/// <summary>
/// Recovers the hosted game's real outcome from the spawner's debugger log.
/// </summary>
/// <remarks>
/// Syringe exits 0 once the process it hosted has gone, whatever happened to
/// it, so its own exit code cannot stand in for the game's. It does record the
/// debuggee's exit code, and it logs unhandled exceptions, which is what this
/// reads.
///
/// Note that <c>SyringeDebugger::HandleException</c> is Syringe's ordinary log
/// channel -- feature-flag notes and hook setup all arrive through it -- so the
/// function name means nothing on its own. Only the message content does.
/// </remarks>
public static class SpawnerCrashDetection
{
    private static readonly Regex exitCodePattern = new(
        @"Run:\s*Done with exit code\s+(-?\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    private static readonly string[] crashMarkers =
    [
        "Exception (Code:",
        "has crashed",
        "Access violation",
    ];

    /// <summary>Returns a short reason when the hosted game did not end cleanly, else null.</summary>
    public static string? FindCrash(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return null;

        foreach (string line in log.Split('\n'))
        {
            foreach (string marker in crashMarkers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase)) return Trim(line);
            }
        }

        // The debuggee's own exit code is the authoritative outcome, and it is
        // the last one logged that counts.
        MatchCollection matches = exitCodePattern.Matches(log);
        if (matches.Count > 0)
        {
            string value = matches[^1].Groups[1].Value;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int exitCode) && exitCode != 0)
                return $"hosted game exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}";
        }
        return null;
    }

    private static string Trim(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length > 240 ? trimmed[..240] : trimmed;
    }
}
