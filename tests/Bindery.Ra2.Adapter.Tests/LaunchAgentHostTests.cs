// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bindery.Ra2.Adapter;
using Xunit;

namespace Bindery.Ra2.Adapter.Tests;

public sealed class LaunchAgentHostTests
{
    private static RemoteLiveClientHost Host(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubAgentHandler(responder)) { BaseAddress = new Uri("http://192.168.122.10:14620/") },
            "192.168.122.10:14620",
            TimeSpan.FromMilliseconds(1));

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(payload, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    [Fact]
    public async Task RemoteRunReportsReadyStartedAndExited()
    {
        List<string> polls = [];
        RemoteLiveClientHost host = Host(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == LaunchAgentProtocol.RunPath) return Json(new LaunchAgentRunResponse("run-1"));
            polls.Add(path);
            return polls.Count < 2
                ? Json(new LaunchAgentRunStatus(LaunchAgentRunStatus.Running, null, null))
                : Json(new LaunchAgentRunStatus(LaunchAgentRunStatus.Exited, 0, null));
        });

        List<LifecycleKind> reports = [];
        int exitCode = await host.RunAsync(
            new SpawnConfiguration("C:/a/gamemd.exe", "MAP01.MAP", "Alice", "192.168.122.1", 50001, SpawnerExecutable: "C:/a/Syringe.exe"),
            new LiveClientLaunch("C:/a/gamemd.exe", "C:/a", "MAP01.MAP", "Alice", SpawnerExecutable: "C:/a/Syringe.exe"),
            "C:/a/SPAWN.INI",
            report => { reports.Add(report.Kind); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal([LifecycleKind.Ready, LifecycleKind.Started, LifecycleKind.Exited], reports);
        Assert.Equal("/run/run-1", polls[0]);
    }

    [Fact]
    public async Task NonZeroRemoteExitIsReportedAsFailed()
    {
        RemoteLiveClientHost host = Host(request => request.RequestUri!.AbsolutePath == LaunchAgentProtocol.RunPath
            ? Json(new LaunchAgentRunResponse("run-2"))
            : Json(new LaunchAgentRunStatus(LaunchAgentRunStatus.Exited, 3, null)));

        List<LifecycleKind> reports = [];
        int exitCode = await host.RunAsync(
            new SpawnConfiguration("C:/a/gamemd.exe", "MAP01.MAP", "Alice", "192.168.122.1", 50001),
            new LiveClientLaunch("C:/a/gamemd.exe", "C:/a", "MAP01.MAP", "Alice"),
            "C:/a/SPAWN.INI",
            report => { reports.Add(report.Kind); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.Equal(3, exitCode);
        Assert.Contains(LifecycleKind.Failed, reports);
        Assert.DoesNotContain(LifecycleKind.Exited, reports);
    }

    [Fact]
    public async Task AgentFailureBecomesAFailedReportAndThrows()
    {
        RemoteLiveClientHost host = Host(request => request.RequestUri!.AbsolutePath == LaunchAgentProtocol.RunPath
            ? Json(new LaunchAgentRunResponse("run-3"))
            : Json(new LaunchAgentRunStatus(LaunchAgentRunStatus.Failed, null, "Win32Exception")));

        List<LifecycleKind> reports = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RunAsync(
            new SpawnConfiguration("C:/a/gamemd.exe", "MAP01.MAP", "Alice", "192.168.122.1", 50001),
            new LiveClientLaunch("C:/a/gamemd.exe", "C:/a", "MAP01.MAP", "Alice"),
            "C:/a/SPAWN.INI",
            report => { reports.Add(report.Kind); return Task.CompletedTask; },
            CancellationToken.None));

        Assert.Contains(LifecycleKind.Failed, reports);
    }

    [Fact]
    public async Task MissingRemoteExecutableIsReportedAgainstTheOwningGuest()
    {
        RemoteLiveClientHost host = Host(_ => Json(new LaunchAgentValidateResponse(false, true)));

        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() => host.ValidateAsync(
            new LiveClientLaunch("C:/a/gamemd.exe", "C:/a", "MAP01.MAP", "Alice"),
            CancellationToken.None));

        Assert.Contains("192.168.122.10", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtocolVersionMismatchIsRejected()
    {
        RemoteLiveClientHost host = Host(_ => Json(new LaunchAgentHealth("bindery-ra2-launch-agent", "bindery.ra2.launch-agent/v0", "bindery-ra2-b", "client-b")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthReportsTheGuestIdentity()
    {
        RemoteLiveClientHost host = Host(_ => Json(new LaunchAgentHealth("bindery-ra2-launch-agent", LaunchAgentProtocol.Version, "bindery-ra2-b", "client-b-b703066a")));

        LaunchAgentHealth health = await host.CheckAsync(CancellationToken.None);

        Assert.Equal("bindery-ra2-b", health.Hostname);
        Assert.Equal("client-b-b703066a", health.ClientInstanceId);
    }

    [Fact]
    public void RemoteSpawnIniPathPointsAtTheGuestTreeNotTheOrchestratorCopy()
    {
        RemoteLiveClientHost host = Host(_ => Json(new { }));

        Assert.Equal(
            "C:\\Bindery\\appliances\\client-b\\SPAWN.INI",
            host.ResolveSpawnIniPath("C:\\Bindery\\appliances\\client-b", "C:\\Temp\\evidence\\spawn-client-b.ini"));
    }

    private sealed class StubAgentHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
