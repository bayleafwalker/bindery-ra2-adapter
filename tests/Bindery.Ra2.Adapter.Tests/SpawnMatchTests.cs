// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class SpawnMatchTests
{
    private static readonly SpawnParticipant PlayerA = new("player-a", 31952, Side: 1, Color: 2, SpawnLocation: 0);
    private static readonly SpawnParticipant PlayerB = new("player-b", 4600, Side: 3, Color: 4, SpawnLocation: 1);

    private static SpawnMatchPlan Plan(bool isHost = true) => isHost
        ? new SpawnMatchPlan("spawnmap.ini", "2022510934", 4242, true, PlayerA, [PlayerB], [PlayerA, PlayerB], [], "192.168.122.1", 50000)
        : new SpawnMatchPlan("spawnmap.ini", "2022510934", 4242, false, PlayerB, [PlayerA], [PlayerA, PlayerB], [], "192.168.122.1", 50000);

    [Fact]
    public void SpawnIniDescribesTheWholeTwoPlayerMatch()
    {
        string ini = SpawnIniRenderer.Render(Plan());

        // The five-key INI that preceded this could not describe a match at
        // all, and the spawner threw on it.
        Assert.Contains("Port=31952", ini, StringComparison.Ordinal);
        Assert.Contains("Host=Yes", ini, StringComparison.Ordinal);
        Assert.Contains("PlayerCount=2", ini, StringComparison.Ordinal);
        Assert.Contains("Seed=4242", ini, StringComparison.Ordinal);
        Assert.Contains("[Other1]", ini, StringComparison.Ordinal);
        Assert.Contains("Name=player-b", ini, StringComparison.Ordinal);
        Assert.Contains("Ip=0.0.0.0", ini, StringComparison.Ordinal);
        Assert.Contains("Port=4600", ini, StringComparison.Ordinal);
        Assert.Contains("[Tunnel]", ini, StringComparison.Ordinal);
        Assert.Contains("Ip=192.168.122.1", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void NetcodeDefaultsMatchWhatTheCnCNetClientForces()
    {
        // Resources/GameOptions.ini [ForcedSpawnIniOptions] in the pinned
        // package, not invention. An earlier guess used Protocol=2 and
        // FrameSendRate=4, and the match connected then starved.
        string ini = SpawnIniRenderer.Render(Plan());

        Assert.Contains("Protocol=0", ini, StringComparison.Ordinal);
        Assert.Contains("FrameSendRate=2", ini, StringComparison.Ordinal);
        Assert.Contains("ReconnectTimeout=1400", ini, StringComparison.Ordinal);
        // Protocol 0 is lockstep; without a lookahead window the spawner has
        // no defined retry behaviour.
        Assert.Contains("MaxAhead=", ini, StringComparison.Ordinal);
        Assert.DoesNotContain("Protocol=2", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void GameModeIsAnIntegerAndSuperweaponsKeepsTheClientsSpelling()
    {
        string ini = SpawnIniRenderer.Render(Plan());

        // INI/MPMapsBase.ini: [BattleForcedSpawnIniOptions] GameMode=1
        Assert.Contains("GameMode=1", ini, StringComparison.Ordinal);
        Assert.Contains("Superweapons=", ini, StringComparison.Ordinal);
        Assert.DoesNotContain("SuperWeapons=", ini, StringComparison.Ordinal);
        Assert.DoesNotContain("GameMode=Battle", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void AiHousesGetAllThreeSectionsAndSeatsAfterTheHumans()
    {
        // AIPlayers=N on its own produces a scenario that never loads: no
        // debug log, no telemetry, black screen with music. Each AI also needs
        // Multi{humans+n} entries in three house sections plus a seat.
        SpawnMatchPlan plan = Plan() with
        {
            AiPlayers = [new SpawnAiParticipant(Handicap: 2, Country: 0, Color: 4, SpawnLocation: 2),
                         new SpawnAiParticipant(Handicap: 2, Country: 5, Color: 5, SpawnLocation: 3)],
        };

        string ini = SpawnIniRenderer.Render(plan);

        Assert.Contains("AIPlayers=2", ini, StringComparison.Ordinal);
        foreach (string section in new[] { "[HouseHandicaps]", "[HouseCountries]", "[HouseColors]" })
        {
            Assert.Contains(section, ini, StringComparison.Ordinal);
        }
        // Two humans, so the AI are Multi3 and Multi4.
        Assert.Contains("Multi3=2", ini, StringComparison.Ordinal);
        Assert.Contains("Multi4=3", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void AiHousesOnOneTeamAreAlliedWithEachOther()
    {
        // Measured 2026-09-27: two unallied AI fought each other for 40
        // minutes and never went near the idle human, so the match could not
        // end. yrpp-spawner reads [Multi{n}_Alliances] HouseAlly{One..} as
        // zero-based house indices and adds them to that house's allies.
        SpawnMatchPlan plan = Plan() with
        {
            AiPlayers = [new SpawnAiParticipant(Country: 2, SpawnLocation: 2, Team: 1),
                         new SpawnAiParticipant(Country: 5, SpawnLocation: 3, Team: 1)],
        };

        string ini = SpawnIniRenderer.Render(plan);

        // Two humans, so the AI are Multi3 (index 2) and Multi4 (index 3).
        Assert.Contains("[Multi3_Alliances]\r\nHouseAllyOne=3\r\n", ini, StringComparison.Ordinal);
        Assert.Contains("[Multi4_Alliances]\r\nHouseAllyOne=2\r\n", ini, StringComparison.Ordinal);
        Assert.DoesNotContain("[Multi1_Alliances]", ini, StringComparison.Ordinal);
        Assert.DoesNotContain("[Multi2_Alliances]", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void AiHousesWithoutATeamGetNoAlliances()
    {
        SpawnMatchPlan plan = Plan() with
        {
            AiPlayers = [new SpawnAiParticipant(SpawnLocation: 2), new SpawnAiParticipant(SpawnLocation: 3)],
        };

        Assert.DoesNotContain("_Alliances]", SpawnIniRenderer.Render(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeTeamIsNoTeam()
    {
        // xna-cncnet-client treats teamId <= 0 as unteamed; so do we.
        SpawnMatchPlan plan = Plan() with
        {
            AiPlayers = [new SpawnAiParticipant(SpawnLocation: 2, Team: -1), new SpawnAiParticipant(SpawnLocation: 3, Team: -1)],
        };

        Assert.DoesNotContain("_Alliances]", SpawnIniRenderer.Render(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void MoreThanEightHousesAreRejected()
    {
        // The spawner reads Multi1..Multi8 only; a ninth house would be
        // rendered as an alliance with whatever sits at house index 8.
        SpawnMatchPlan plan = Plan() with
        {
            AiPlayers = Enumerable.Range(0, 7).Select(static _ => new SpawnAiParticipant(Team: 1)).ToArray(),
        };

        Assert.Throws<ArgumentException>(() => SpawnIniRenderer.Render(plan));
    }

    [Fact]
    public void TheGameIsToldToExitWithoutTheScoreScreen()
    {
        // Otherwise the match ends but the process waits on a click, and the
        // only way to finish a run is to kill it -- which makes an aborted run
        // look like a completed one.
        string ini = SpawnIniRenderer.Render(Plan());

        Assert.Contains("SkipScoreScreen=Yes", ini, StringComparison.Ordinal);
        Assert.Contains("QuickExit=Yes", ini, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyOneClientIsHost()
    {
        Assert.Contains("Host=Yes", SpawnIniRenderer.Render(Plan(isHost: true)), StringComparison.Ordinal);
        Assert.Contains("Host=No", SpawnIniRenderer.Render(Plan(isHost: false)), StringComparison.Ordinal);
    }

    [Fact]
    public void ParticipantsSharingATunnelPortAreRejected()
    {
        SpawnParticipant collidingB = PlayerB with { TunnelPort = 31952 };
        SpawnMatchPlan collided = Plan() with { Others = [collidingB], GlobalOrder = [PlayerA, collidingB] };

        Assert.Throws<ArgumentException>(() => SpawnIniRenderer.Render(collided));
    }

    [Fact]
    public void AParticipantWithoutAnAllocatedPortIsRejected()
    {
        SpawnParticipant unallocatedB = PlayerB with { TunnelPort = 0 };
        SpawnMatchPlan unallocated = Plan() with { Others = [unallocatedB], GlobalOrder = [PlayerA, unallocatedB] };

        Assert.Throws<ArgumentOutOfRangeException>(() => SpawnIniRenderer.Render(unallocated));
    }

    [Fact]
    public void SpawnLocationsAreIdenticalOnBothClients()
    {
        // The section is written in global player order, so the host's copy
        // and the joiner's copy must agree exactly. Ordering it relative to
        // the local player desyncs the match on frame one.
        string host = SpawnIniRenderer.Render(Plan(isHost: true));
        string joiner = SpawnIniRenderer.Render(Plan(isHost: false));

        string Section(string ini) => ini[ini.IndexOf("[SpawnLocations]", StringComparison.Ordinal)..];

        Assert.Equal(Section(host), Section(joiner));
        Assert.Contains("Multi1=0", host, StringComparison.Ordinal);
        Assert.Contains("Multi2=1", host, StringComparison.Ordinal);
    }

    [Fact]
    public void AGlobalOrderMissingAParticipantIsRejected()
    {
        SpawnMatchPlan wrong = Plan() with { GlobalOrder = [PlayerA, PlayerA] };

        Assert.Throws<ArgumentException>(() => SpawnIniRenderer.Render(wrong));
    }

    [Fact]
    public void TunnelPortsAreParsedFromTheV2Response()
    {
        Assert.Equal([31952, 4600], TunnelPortAllocation.Parse("[31952,4600]", 2));
    }

    [Fact]
    public void TooFewAllocatedPortsIsAnError()
    {
        Assert.Throws<InvalidOperationException>(() => TunnelPortAllocation.Parse("[31952]", 2));
    }

    [Fact]
    public void DuplicateAllocatedPortsAreRejected()
    {
        // Both clients on one channel looks like a working match until it desyncs.
        Assert.Throws<InvalidOperationException>(() => TunnelPortAllocation.Parse("[31952,31952]", 2));
    }

    [Fact]
    public void DebuggerExceptionLinesAreRecordedAsEventsNotFailures()
    {
        // Syringe logs first-chance exceptions: ones the game throws and
        // handles. RA2 with Ares does this routinely in a match that plays and
        // exits normally, so these are events, not verdicts.
        IReadOnlyList<RunObservation> observations = SpawnerLogObservations.Read(
            "[21:09:19] SyringeDebugger::HandleException: Exception (Code: 0xE06D7363 at 0x7689A2B4)!\n" +
            "[21:09:48] SyringeDebugger::Run: Done with exit code 3 (3).\n");

        RunObservation exception = Assert.Single(observations, o => o.Kind == RunObservation.SpawnerException);
        Assert.False(exception.Notable);
        Assert.All(observations, o => Assert.False(o.Notable));
    }

    [Fact]
    public void TheHostedExitCodeIsRecordedWithoutJudgingIt()
    {
        // Yuri's Revenge exits with 3 on an ordinary quit from the score screen.
        IReadOnlyList<RunObservation> observations = SpawnerLogObservations.Read(
            "[21:09:48] SyringeDebugger::Run: Done with exit code 3 (3).\n");

        RunObservation exitCode = Assert.Single(observations, o => o.Kind == RunObservation.HostedExitCode);
        Assert.Contains("3", exitCode.Detail, StringComparison.Ordinal);
        Assert.False(exitCode.Notable);
    }

    [Fact]
    public void AnExceptionStatusAsTheExitCodeIsACrash()
    {
        // Lab run 20260928-123407-s2: client-b's game died at startup with
        // STATUS_STACK_OVERFLOW, the observation was not notable, and the
        // channel recorded the match as completed.
        IReadOnlyList<RunObservation> observations = SpawnerLogObservations.Read(
            "[02:45:16] SyringeDebugger::Run: Done with exit code C00000FD (3221225725).\n");

        RunObservation exitCode = Assert.Single(observations, o => o.Kind == RunObservation.HostedExitCode);
        Assert.True(exitCode.Notable);
        Assert.Contains("C00000FD", exitCode.Detail, StringComparison.Ordinal);
        Assert.Contains("crash", exitCode.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheLastExitLineOfARealLogDecides()
    {
        // The real layout: CRLF lines, first-chance exceptions, lines after the exit.
        const string log = "[02:45:13] SyringeDebugger::HandleException: Exception (Code: 0xE06D7363 at 0x7556AED4)!\r\n" +
                           "[02:45:14] SyringeDebugger::Run: Done with exit code 3 (3).\r\n" +
                           "[02:45:16] SyringeDebugger::Run: Done with exit code C0000005 (3221225477).\r\n" +
                           "\r\n[02:45:16] WinMain: SyringeDebugger::Run finished.\r\n[02:45:16] WinMain: Exiting on success.\r\n";

        RunObservation exitCode = Assert.Single(SpawnerLogObservations.Read(log), o => o.Kind == RunObservation.HostedExitCode);
        Assert.True(exitCode.Notable);
        Assert.StartsWith("Done with exit code C0000005 (3221225477).", exitCode.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AKilledGameIsNotReportedAsACrash()
    {
        // The lab's timeout stops the process: Syringe records FFFFFFFF.
        IReadOnlyList<RunObservation> observations = SpawnerLogObservations.Read(
            "[22:23:02] SyringeDebugger::Run: Done with exit code FFFFFFFF (4294967295).\n");

        Assert.False(Assert.Single(observations, o => o.Kind == RunObservation.HostedExitCode).Notable);
    }

    [Fact]
    public void RoutineHookWarningsProduceNoObservations()
    {
        // Syringe reports these on every RA2 run.
        Assert.Empty(SpawnerLogObservations.Read(
            "[18:48:39] SyringeDebugger::RebuildInstructions: Failed to decode instruction at 0x00416C4E.\n" +
            "[18:48:39] SyringeDebugger::HandleException: Finished setting feature flags.\n"));
    }

    [Fact]
    public void ADesyncIsNotableAndIsNotCalledACrash()
    {
        RunObservation desync = new(RunObservation.Desync, "the game wrote SYNC0.TXT", Notable: true);

        Assert.True(desync.Notable);
        Assert.DoesNotContain("crash", desync.Kind, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnySynchronizationDumpIsNotableAndEmptyFilesStillCount()
    {
        IReadOnlyList<RunObservation> observations = DesyncObservations.Read(new Dictionary<string, string?>
        {
            ["SYNC0.TXT"] = null,
            ["SYNC1.TXT"] = string.Empty,
            ["SYNC2.TXT"] = null,
        });

        RunObservation observation = Assert.Single(observations);
        Assert.Equal(RunObservation.Desync, observation.Kind);
        Assert.Equal("the game wrote SYNC1.TXT", observation.Detail);
        Assert.True(observation.Notable);
    }

    [Fact]
    public void DesyncScanCoversAllKnownGameDumpNames()
    {
        Assert.Equal(["SYNC0.TXT", "SYNC1.TXT", "SYNC2.TXT"], DesyncObservations.LogNames);
    }

}
