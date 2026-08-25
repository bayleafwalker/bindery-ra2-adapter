// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bindery.Ra2.Adapter;

/// <summary>One participant as the CnCNet spawner needs to see them.</summary>
/// <remarks>
/// <paramref name="TunnelPort"/> is the port the tunnel allocated for this
/// player, not a port on any host: in tunnel play every participant is
/// addressed as 0.0.0.0 on their allocated tunnel port.
/// </remarks>
public sealed record SpawnParticipant(
    string Name,
    int TunnelPort,
    int Side = 0,
    int Color = 0,
    int SpawnLocation = -1,
    bool IsSpectator = false);

/// <summary>
/// Match rules, using the spawner's own option names.
/// </summary>
/// <remarks>
/// The defaults are taken from what the real CnCNet client forces, in
/// <c>Resources/GameOptions.ini</c> under <c>[ForcedSpawnIniOptions]</c>, and
/// from <c>INI/MPMapsBase.ini</c> for the game mode. They are not invented:
/// an earlier guess used <c>Protocol=2</c>, <c>FrameSendRate=4</c> and a
/// <c>GameMode</c> string, all of which differ from what the client writes.
/// <c>GameMode</c> is an integer (1 = Battle) and the spelling is
/// <c>Superweapons</c>, not <c>SuperWeapons</c>.
/// </remarks>
public sealed record SpawnGameOptions(
    int GameSpeed = 3,
    int Credits = 10000,
    int UnitCount = 10,
    bool ShortGame = true,
    bool Superweapons = false,
    bool Crates = false,
    bool BuildOffAlly = false,
    bool MultiEngineer = false,
    bool Bases = true,
    bool MCVRedeploy = true,
    bool FogOfWar = false,
    bool SidebarHack = true,
    bool AINamesByDifficulty = true,
    bool SpecialHouseIsAlly = false,
    bool DefeatedBecomesObserver = true,
    int GameMode = 1,
    int FrameSendRate = 2,
    int Protocol = 0,
    int ReconnectTimeout = 1400,
    int MaxAhead = 10);

/// <summary>
/// Everything one client needs to join one match. The five-key INI the adapter
/// used to emit could not describe a two-player game at all -- no ports, no
/// host election, no opponent -- and the spawner threw on it.
/// </summary>
public sealed record SpawnMatchPlan(
    string MapId,
    string GameId,
    int Seed,
    bool IsHost,
    SpawnParticipant Local,
    IReadOnlyList<SpawnParticipant> Others,
    IReadOnlyList<SpawnParticipant> GlobalOrder,
    string TunnelHost,
    int TunnelPort,
    SpawnGameOptions? Options = null,
    string PeerAddress = "0.0.0.0")
{
    public SpawnGameOptions EffectiveOptions => Options ?? new SpawnGameOptions();

    public int PlayerCount => 1 + Others.Count;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(MapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(GameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(TunnelHost);
        ArgumentNullException.ThrowIfNull(Local);
        ArgumentNullException.ThrowIfNull(Others);
        if (Others.Count == 0) throw new ArgumentException("a spawn plan needs at least one other participant");
        if (TunnelPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(TunnelPort));
        foreach (SpawnParticipant participant in Others.Prepend(Local))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(participant.Name);
            if (participant.TunnelPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(participant.TunnelPort), $"{participant.Name} has no allocated tunnel port");
        }
        // [SpawnLocations] is written in GLOBAL player order and must be byte
        // identical on every client: xna-cncnet-client writes Multi{pId+1} from
        // its global Players list, and each client derives the same assignment
        // deterministically. Ordering it relative to the local player makes the
        // two clients disagree about who starts where, which desyncs the match
        // on frame one.
        ArgumentNullException.ThrowIfNull(GlobalOrder);
        if (GlobalOrder.Count != PlayerCount) throw new ArgumentException("the global order must contain every participant exactly once");
        if (!GlobalOrder.Contains(Local)) throw new ArgumentException("the local participant is missing from the global order");
        foreach (SpawnParticipant other in Others)
        {
            if (!GlobalOrder.Contains(other)) throw new ArgumentException($"{other.Name} is missing from the global order");
        }

        // Two players sharing one allocated port would silently collide on the
        // tunnel rather than fail loudly.
        int[] ports = Others.Prepend(Local).Select(static p => p.TunnelPort).ToArray();
        if (ports.Distinct().Count() != ports.Length) throw new ArgumentException("each participant needs a distinct tunnel port");
    }
}
