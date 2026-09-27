// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter.Channel;
using Bindery.Ra2.Adapter.Ra2yrcpp;
using Ra2Yrproto.Commands;
using Ra2Yrproto.Ra2Yr;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class Ra2yrcppChannelSettingsTests
{
    private static readonly JsonSerializerOptions web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AnAgentSeatFromSettingsDeclaresThePlaybookControllerForItsHouseAndClient()
    {
        Ra2yrcppAgentSeatSettings settings = JsonSerializer.Deserialize<Ra2yrcppAgentSeatSettings>(
            "{\"house\":\"player-b\",\"clientInstanceId\":\"machine-b\",\"commandEndpoint\":\"192.168.122.10:14521\",\"routine\":\"deploy_mcv\"}", web)!;
        settings.Validate();

        AgentSeatAssignment assignment = settings.ToAssignment();
        Assert.Equal("player-b", assignment.House);
        Assert.Equal("machine-b", assignment.ClientInstanceId);
        Assert.Equal(ControllerDeclaration.Agent("bindery.playbook/rules/v1", "0.1.0"), assignment.Controller);
        assignment.Validate();
        (AgentSeat seat, Ra2yrcppCommandSink commands) = settings.CreateSeat();
        Assert.Equal("player-b", seat.House);
        Assert.Equal("player-b", commands.House);
    }

    [Theory]
    [InlineData("{\"house\":\"\",\"clientInstanceId\":\"m\",\"commandEndpoint\":\"h:14521\"}")]
    [InlineData("{\"house\":\"p\",\"clientInstanceId\":\"m\",\"commandEndpoint\":\"no-port\"}")]
    [InlineData("{\"house\":\"p\",\"clientInstanceId\":\"m\",\"commandEndpoint\":\"h:14521\",\"routine\":\"rush\"}")]
    public void AnIncompleteAgentSeatIsRejected(string json)
    {
        Ra2yrcppAgentSeatSettings settings = JsonSerializer.Deserialize<Ra2yrcppAgentSeatSettings>(json, web)!;

        Assert.ThrowsAny<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void LiveTelemetryFromSettingsReadsTheNamedService()
    {
        Ra2yrcppLiveTelemetrySettings settings = JsonSerializer.Deserialize<Ra2yrcppLiveTelemetrySettings>("{\"endpoint\":\"192.168.122.11:14521\",\"pollMilliseconds\":250}", web)!;
        settings.Validate();

        IRa2TelemetrySource source = settings.Create();

        Assert.IsType<Ra2yrcppTelemetrySource>(source);
        Assert.Equal(new Ra2YrcppEndpoint("192.168.122.11", 14521), source.Capture.Endpoint);
        Assert.Throws<ArgumentException>(new Ra2yrcppLiveTelemetrySettings { Endpoint = "192.168.122.11" }.Validate);
        Assert.ThrowsAny<ArgumentException>(new Ra2yrcppLiveTelemetrySettings { Endpoint = "h:1", PollMilliseconds = 0 }.Validate);
    }

    [Fact]
    public async Task AConfiguredMcvSeatDeploysItsOwnMcvThroughTheLiveSink()
    {
        FakeGame game = new();
        await using FakeRa2yrcppServer server = new(game.Handle);
        Ra2yrcppAgentSeatSettings settings = new()
        {
            House = "Americans",
            ClientInstanceId = "machine-a",
            CommandEndpoint = $"{server.Uri.Host}:{server.Uri.Port}",
            Routine = Ra2yrcppAgentSeatSettings.DeployMcvRoutine,
        };
        (AgentSeat seat, Ra2yrcppCommandSink commands) = settings.CreateSeat();
        await using Ra2yrcppCommandSink _ = commands;
        string directory = Path.Combine(Path.GetTempPath(), "bindery-seat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string recording = Path.Combine(directory, "telemetry.ndjson");
            await File.WriteAllLinesAsync(recording,
            [
                Line(1, Ra2TelemetryEventTypes.MatchStarted, "{}"),
                Line(2, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Soviets\",\"type\":\"SMCV\",\"object\":177,\"visible_to\":[\"Soviets\"]}"),
                Line(3, Ra2TelemetryEventTypes.UnitCreated, "{\"house\":\"Americans\",\"type\":\"AMCV\",\"object\":161,\"visible_to\":[\"Americans\"]}"),
            ]);

            AgentSeatSummary summary = await seat.RunAsync(new NdjsonTelemetrySource(recording), directory);

            Assert.Equal(1, summary.CommandsSent);
            Assert.Equal(1, summary.ObservationsWithheld);
            UnitOrder order = Assert.IsType<UnitOrder>(Assert.Single(game.Orders));
            Assert.Equal(UnitAction.Deploy, order.Action);
            Assert.Equal([0xA1u], order.ObjectAddresses);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Line(ulong sequence, string type, string payload) =>
        NdjsonTelemetryFormat.Serialize(new RawObservation($"e{sequence}", "c1", sequence, type, Ra2LabProfile.AdapterId, Ra2LabProfile.AdapterVersion, DateTimeOffset.UnixEpoch.AddSeconds(sequence), JsonDocument.Parse(payload).RootElement.Clone(), "sha256:raw"));
}
