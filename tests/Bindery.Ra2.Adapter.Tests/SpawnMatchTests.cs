// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class SpawnMatchTests
{
    private static readonly SpawnParticipant PlayerA = new("player-a", 31952, Side: 1, Color: 2, SpawnLocation: 0);
    private static readonly SpawnParticipant PlayerB = new("player-b", 4600, Side: 3, Color: 4, SpawnLocation: 1);

    private static SpawnMatchPlan Plan(bool isHost = true) => isHost
        ? new SpawnMatchPlan("spawnmap.ini", "2022510934", 4242, true, PlayerA, [PlayerB], [PlayerA, PlayerB], "192.168.122.1", 50000)
        : new SpawnMatchPlan("spawnmap.ini", "2022510934", 4242, false, PlayerB, [PlayerA], [PlayerA, PlayerB], "192.168.122.1", 50000);

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

}
