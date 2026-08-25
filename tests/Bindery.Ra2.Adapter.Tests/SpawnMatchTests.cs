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

    [Theory]
    [InlineData("[18:13:40] SyringeDebugger::HandleException: Exception (Code: 0xE06D7363 at 0x76003874)!")]
    [InlineData("gamemd.exe has crashed")]
    [InlineData("Access violation at 0x00401000")]
    public void SpawnerCrashesAreDetectedInTheDebuggerLog(string line)
    {
        Assert.NotNull(SpawnerCrashDetection.FindCrash($"[18:13:39] starting\n{line}\n"));
    }

    [Fact]
    public void TheHostedGamesExitCodeIsRecoveredFromTheLog()
    {
        // Syringe exits 0 regardless; the debuggee's code is the real outcome.
        string? reason = SpawnerCrashDetection.FindCrash(
            "[18:49:36] SyringeDebugger::Run: Waiting for process to exit...\n" +
            "[18:49:37] SyringeDebugger::Run: Done with exit code 3 (3).\n" +
            "[18:49:37] WinMain: Exiting on success.\n");

        Assert.NotNull(reason);
        Assert.Contains("3", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void HandleExceptionIsSyringesOrdinaryLogChannelNotACrash()
    {
        // This is the false positive that made a working run look failed:
        // routine feature-flag and hook-setup notes are logged through a
        // function called HandleException, so the name alone means nothing.
        Assert.Null(SpawnerCrashDetection.FindCrash(
            "[18:48:39] SyringeDebugger::HandleException: Feature flag \"ZFPreservation\" not exported by \"libra2yrcpp.dll\", skipping.\n" +
            "[18:48:39] SyringeDebugger::HandleException: Finished setting feature flags.\n" +
            "[18:48:39] SyringeDebugger::HandleException: Creating code hooks.\n" +
            "[18:49:37] SyringeDebugger::Run: Done with exit code 0 (0).\n"));
    }

    [Fact]
    public void FailedInstructionDecodesAreNotCrashes()
    {
        // Syringe reports these on every RA2 run; they are warnings about hook
        // sites, not failures.
        Assert.Null(SpawnerCrashDetection.FindCrash(
            "[18:48:39] SyringeDebugger::RebuildInstructions: Failed to decode instruction at 0x00416C4E, copying remaining 4 bytes verbatim.\n" +
            "[18:49:37] SyringeDebugger::Run: Done with exit code 0 (0).\n"));
    }

    [Fact]
    public void ACleanSpawnerLogIsNotACrash()
    {
        Assert.Null(SpawnerCrashDetection.FindCrash("[18:13:39] Syringe 0.8\n[18:13:40] Loading gamemd.exe\n"));
    }

    [Fact]
    public void NoSpawnerLogIsNotACrash()
    {
        // A spawner that logs nothing cannot be checked this way; the evidence
        // records that as a limitation rather than pretending it passed.
        Assert.Null(SpawnerCrashDetection.FindCrash(null));
    }
}
