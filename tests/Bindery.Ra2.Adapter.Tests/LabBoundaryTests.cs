// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class LabBoundaryTests
{
    [Fact]
    public void SpawnIniUsesMaintainedYrppSpawnerKeys()
    {
        string ini = SpawnIniRenderer.Render(new SpawnConfiguration(
            "C:/Bindery/client-a/gamemd.exe",
            "MAP01.MAP",
            "alice",
            "tunnel.test",
            1234));

        Assert.Contains("[Settings]", ini);
        Assert.Contains("Scenario=MAP01.MAP", ini);
        Assert.Contains("Ra2Mode=true", ini);
        Assert.Contains("ForceMultiplayer=true", ini);
        Assert.Contains("WriteStatistics=true", ini);
        Assert.Contains("Name=alice", ini);
        Assert.Contains("[Tunnel]", ini);
        Assert.Contains("Ip=tunnel.test", ini);
        Assert.DoesNotContain("[Multiplayer]", ini);
        Assert.DoesNotContain("GameExecutable=", ini);
    }

    [Fact]
    public async Task PrivateTunnelBoundaryRejectsNativePlacement()
    {
        RelayPlacement placement = new(
            "eu-north",
            "bindery-native",
            "0198c2c3-4d5e-7f60-8123-456789abcdef",
            "127.0.0.1:1234",
            "relay-placement/v1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CncNetPrivateTunnelBoundary().AttachAsync(placement));
    }

    [Fact]
    public void PrivateProviderDoesNotAcceptNativeCredential()
    {
        AdapterConfiguration configuration = new(
            new Uri("https://control-plane.test"),
            "account-token",
            "join-token",
            "0198c2c3-4d5e-7f70-8123-456789abcdef",
            "instance-a",
            "0198c2c3-4d5e-7f71-8123-456789abcdef",
            "lease-token",
            ClientClass.Player,
            new AdapterIdentity(Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion),
            new CompatibilityHashes("sha256:game", "sha256:mod", "sha256:map"),
            RelayProvider.CncNetPrivate,
            "127.0.0.1",
            1234,
            "native-credential");

        Assert.Throws<InvalidOperationException>(() => RelaySelection.Validate(configuration));
    }

    [Fact]
    public void NormalizerPreservesExtendedYrEventVocabulary()
    {
        using JsonDocument document = JsonDocument.Parse("{\"unit_id\":7}");
        RawObservation observation = new(
            "event-1",
            "capture-1",
            1,
            Ra2TelemetryEventTypes.UnitCreated,
            Ra2LabProfile.AdapterId,
            Ra2LabProfile.AdapterVersion,
            DateTimeOffset.UtcNow,
            document.RootElement,
            "sha256:raw");

        NormalizedObservation result = Assert.Single(new Ra2Normalizer().Normalize([observation]));

        Assert.Equal("game.unit.created", result.EventType);
        Assert.Equal("event-1", Assert.Single(result.SourceEventIds));
    }
}
