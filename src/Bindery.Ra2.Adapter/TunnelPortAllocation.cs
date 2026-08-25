// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bindery.Ra2.Adapter;

/// <summary>
/// Asks the private CnCNet tunnel for one port per participant.
/// </summary>
/// <remarks>
/// The spawner addresses every peer as 0.0.0.0 on a tunnel-allocated port, so
/// without this call there are no ports to write into the spawn INI and no
/// match can form. The V2 endpoint answers with a JSON-ish array of ports,
/// e.g. <c>[31952,4600]</c>.
/// </remarks>
public static class TunnelPortAllocation
{
    private static readonly Regex portPattern = new(@"\d+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static async Task<IReadOnlyList<int>> RequestAsync(HttpClient client, Uri tunnelV2, int participants, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tunnelV2);
        if (participants < 2) throw new ArgumentOutOfRangeException(nameof(participants), "a match needs at least two participants");

        Uri request = new(tunnelV2, $"/request?clients={participants.ToString(CultureInfo.InvariantCulture)}");
        using HttpResponseMessage response = await client.GetAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(body, participants);
    }

    internal static IReadOnlyList<int> Parse(string body, int participants)
    {
        ArgumentNullException.ThrowIfNull(body);
        int[] ports = portPattern.Matches(body)
            .Select(static match => int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) ? port : -1)
            .Where(static port => port is > 0 and <= 65535)
            .ToArray();
        if (ports.Length < participants)
            throw new InvalidOperationException($"tunnel allocated {ports.Length} port(s) for {participants} participants: '{body.Trim()}'");
        // A tunnel that hands out one port twice would put both clients on the
        // same channel, which looks like a working match until it desyncs.
        int[] taken = ports.Take(participants).ToArray();
        if (taken.Distinct().Count() != taken.Length)
            throw new InvalidOperationException($"tunnel allocated duplicate ports: '{body.Trim()}'");
        return taken;
    }
}
