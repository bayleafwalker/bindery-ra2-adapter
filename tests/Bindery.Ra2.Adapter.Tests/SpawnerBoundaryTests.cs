// SPDX-License-Identifier: GPL-3.0-or-later
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class SpawnerBoundaryTests
{
    [Fact]
    public void SyringeReceivesGameArgumentsAsOneArgsValue()
    {
        IReadOnlyList<string> arguments = WindowsSpawnerBoundary.BuildSyringeArguments(new SpawnConfiguration(
            "gamemd.exe",
            "MAP01.MAP",
            "Alice",
            "127.0.0.1",
            50001,
            SpawnerExecutable: "Syringe.exe"));

        Assert.Equal(["gamemd.exe", "--args=-SPAWN -CD -LOG"], arguments);
    }

    [Fact]
    public void BlankSpawnerArgumentIsRejected()
    {
        Assert.Throws<ArgumentException>(() => WindowsSpawnerBoundary.BuildGameArguments(new SpawnConfiguration(
            "gamemd.exe",
            "MAP01.MAP",
            "Alice",
            "127.0.0.1",
            50001,
            SpawnerArguments: ["-SPAWN", ""])));
    }
}
