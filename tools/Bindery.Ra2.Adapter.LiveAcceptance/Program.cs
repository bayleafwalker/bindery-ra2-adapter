// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: Bindery.Ra2.Adapter.LiveAcceptance <settings.json>");
    return 2;
}

try
{
    LiveAcceptanceSettings settings = JsonSerializer.Deserialize<LiveAcceptanceSettings>(await File.ReadAllTextAsync(args[0]), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("settings file was empty");
    Uri serviceUri = new(settings.ServiceUri, UriKind.Absolute);
    // A match runs for minutes with no control-plane traffic in between, so a
    // pooled connection can go stale and the next report fails with
    // HttpRequestException. Retire idle connections rather than reusing a dead
    // one, and allow for the game holding the machine busy.
    SocketsHttpHandler handler = new()
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
    using HttpClient httpClient = new(handler) { BaseAddress = serviceUri, Timeout = TimeSpan.FromSeconds(60) };
    BinderyAdapterClient controlPlane = new(httpClient);
    ILiveMatchDriver driver = settings.TransportProvider switch
    {
        Ra2LabProfile.TransportProviderId => new CncNetPrivateMatchDriver(controlPlane, serviceUri),
        "bindery-native" => new TwoClientMatchDriver(controlPlane, serviceUri),
        _ => throw new ArgumentException($"unsupported live transport provider '{settings.TransportProvider}'")
    };
    // Each client is owned by a host: this machine, or another cloned guest
    // reached through its launch agent. Two guests is what
    // docs/golden-appliance.md requires; one machine remains supported for
    // fixture runs and is recorded as a limitation in the evidence.
    ISpawnerBoundary spawner = new WindowsSpawnerBoundary();
    (ILiveClientHost firstHost, HttpClient? firstAgent) = await LiveHosts.BuildAsync(settings.FirstHost, spawner);
    (ILiveClientHost secondHost, HttpClient? secondAgent) = await LiveHosts.BuildAsync(settings.SecondHost, spawner);
    using HttpClient? firstAgentLifetime = firstAgent;
    using HttpClient? secondAgentLifetime = secondAgent;
    (ILiveClientHost? observerHost, HttpClient? observerAgent) = settings.HasObserver
        ? await LiveHosts.BuildAsync(settings.ObserverHost, spawner)
        : (null, null);
    using HttpClient? observerAgentLifetime = observerAgent;
    if (firstHost is LocalLiveClientHost && secondHost is LocalLiveClientHost)
        Console.Error.WriteLine("warning: both clients are local; machine identity is NOT divergent for this run");

    await settings.ValidateAsync(firstHost, secondHost, observerHost);
    LiveAcceptanceRunner runner = new(driver, controlPlane, firstHost, secondHost, observerHost);

    // Idempotency keys make a retry safe; they must not be reused for a new
    // match. A replayed session create returns no one-time join credential, so
    // a fixed key in the settings file breaks every run after the first.
    settings = settings.WithFreshIdempotencyKeys();
    Console.WriteLine($"session_idempotency_key={settings.SessionIdempotencyKey}");

    LiveAcceptanceRequest request = settings.ToRequest(settings.EvidenceDirectory);

    LiveAcceptanceEvidence evidence = await runner.RunAsync(request);
    Console.WriteLine($"evidence={Path.Combine(settings.EvidenceDirectory, "live-acceptance-evidence.json")}");
    Console.WriteLine($"session_id={evidence.SessionId}");
    Console.WriteLine($"control_plane_lifecycle_complete={evidence.Qualification.ControlPlaneLifecycleComplete}");
    Console.WriteLine("qualification_eligible=false (external relay, Kctl, oracle, and human gates remain)");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"live acceptance failed: {exception.Message}");
    return 1;
}
