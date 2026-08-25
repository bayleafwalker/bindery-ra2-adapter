// SPDX-License-Identifier: GPL-3.0-or-later
// Launch agent: runs inside a cloned RA2/YR guest so the acceptance
// orchestrator on the other guest can drive this client's game process.
//
// It exposes exactly the five operations in LaunchAgentProtocol. There is no
// generic command endpoint: the only executable it will ever start is the one
// named in the request, and the caller must present the shared token.
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Bindery.Ra2.Adapter;

bool allowExec = args.Contains("--allow-exec", StringComparer.Ordinal);
string[] positional = args.Where(static a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (positional.Length is < 1 or > 2)
{
    Console.Error.WriteLine("usage: Bindery.Ra2.Adapter.LaunchAgent <listen-prefix> [token-file] [--allow-exec]");
    Console.Error.WriteLine("  example: Bindery.Ra2.Adapter.LaunchAgent http://192.168.122.10:14620/ C:\\private\\agent-token.txt");
    Console.Error.WriteLine("  --allow-exec adds a diagnostic shell endpoint; leave it off for acceptance runs");
    return 2;
}

string prefix = positional[0].EndsWith('/') ? positional[0] : positional[0] + "/";
string tokenFile = positional.Length == 2 ? positional[1] : "C:\\private\\agent-token.txt";
if (!File.Exists(tokenFile))
{
    Console.Error.WriteLine($"token file not found: {tokenFile}");
    return 2;
}
string token = (await File.ReadAllTextAsync(tokenFile)).Trim();
if (token.Length < 32)
{
    Console.Error.WriteLine("agent token must be at least 32 characters");
    return 2;
}

JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
Dictionary<string, RunState> runs = [];
object runsGate = new();

using HttpListener listener = new();
listener.Prefixes.Add(prefix);
try
{
    listener.Start();
}
catch (HttpListenerException exception)
{
    Console.Error.WriteLine($"cannot listen on {prefix}: {exception.Message}");
    Console.Error.WriteLine("run as administrator, or grant the URL with: netsh http add urlacl url=<prefix> user=%USERNAME%");
    return 1;
}

Console.WriteLine($"{LaunchAgentProtocol.Version} listening on {prefix}");
Console.WriteLine($"host={Environment.MachineName} client_instance={Environment.GetEnvironmentVariable("BINDERY_CLIENT_INSTANCE_ID") ?? "(unset)"}");
Console.WriteLine(allowExec
    ? "diagnostic shell ENABLED (--allow-exec): this agent will run arbitrary commands for anyone holding the token"
    : "diagnostic shell disabled");

using CancellationTokenSource shutdown = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

while (!shutdown.IsCancellationRequested)
{
    HttpListenerContext context;
    try { context = await listener.GetContextAsync(); }
    catch (HttpListenerException) { break; }
    _ = Task.Run(() => HandleAsync(context));
}
return 0;

async Task HandleAsync(HttpListenerContext context)
{
    try
    {
        string? authorization = context.Request.Headers["Authorization"];
        if (authorization != $"Bearer {token}")
        {
            await WriteAsync(context, 401, new { error = "unauthorized" });
            return;
        }

        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') is { Length: > 0 } p ? p : "/";
        string method = context.Request.HttpMethod;

        if (method == "GET" && path == LaunchAgentProtocol.HealthPath)
        {
            await WriteAsync(context, 200, new LaunchAgentHealth(
                "bindery-ra2-launch-agent",
                LaunchAgentProtocol.Version,
                Environment.MachineName,
                Environment.GetEnvironmentVariable("BINDERY_CLIENT_INSTANCE_ID")));
            return;
        }

        if (method == "POST" && path == LaunchAgentProtocol.ValidatePath)
        {
            LaunchAgentValidateRequest request = await ReadAsync<LaunchAgentValidateRequest>(context);
            await WriteAsync(context, 200, new LaunchAgentValidateResponse(
                File.Exists(request.GameExecutable),
                Directory.Exists(request.WorkingDirectory)));
            return;
        }

        if (method == "POST" && path == LaunchAgentProtocol.HashPath)
        {
            LaunchAgentHashRequest request = await ReadAsync<LaunchAgentHashRequest>(context);
            if (!File.Exists(request.Path)) { await WriteAsync(context, 404, new { error = "not found", request.Path }); return; }
            await WriteAsync(context, 200, new LaunchAgentHashResponse(Hashing.Sha256File(request.Path)));
            return;
        }

        if (method == "POST" && path == LaunchAgentProtocol.SpawnIniRestorePath)
        {
            LaunchAgentSpawnIniRequest request = await ReadAsync<LaunchAgentSpawnIniRequest>(context);
            string runtime = Path.Combine(request.WorkingDirectory, "SPAWN.INI");
            string backup = runtime + ".bindery-backup";
            if (File.Exists(backup)) { File.Copy(backup, runtime, overwrite: true); File.Delete(backup); }
            else if (File.Exists(runtime)) { File.Delete(runtime); }
            await WriteAsync(context, 200, new LaunchAgentSpawnIniResponse(false));
            return;
        }

        if (method == "POST" && path == LaunchAgentProtocol.SpawnIniPath)
        {
            LaunchAgentSpawnIniRequest request = await ReadAsync<LaunchAgentSpawnIniRequest>(context);
            if (!Directory.Exists(request.WorkingDirectory)) { await WriteAsync(context, 404, new { error = "working directory not found" }); return; }
            string runtime = Path.Combine(request.WorkingDirectory, "SPAWN.INI");
            string backup = runtime + ".bindery-backup";
            bool preserved = File.Exists(runtime);
            if (preserved) File.Copy(runtime, backup, overwrite: true);
            await File.WriteAllTextAsync(runtime, request.Content ?? string.Empty);
            await WriteAsync(context, 200, new LaunchAgentSpawnIniResponse(preserved));
            return;
        }

        if (method == "POST" && (path == LaunchAgentProtocol.LogResetPath || path == LaunchAgentProtocol.LogReadPath))
        {
            LaunchAgentLogRequest request = await ReadAsync<LaunchAgentLogRequest>(context);
            // Only the spawner's own log inside the client's working directory
            // is readable: no arbitrary path may be exfiltrated through this.
            string name = Path.GetFileName(request.LogName);
            if (string.IsNullOrWhiteSpace(name)) { await WriteAsync(context, 400, new { error = "log name required" }); return; }
            string logPath = Path.Combine(request.WorkingDirectory, name);
            if (path == LaunchAgentProtocol.LogResetPath)
            {
                if (File.Exists(logPath)) File.Delete(logPath);
                await WriteAsync(context, 200, new LaunchAgentLogResponse(null, false));
                return;
            }
            if (!File.Exists(logPath)) { await WriteAsync(context, 200, new LaunchAgentLogResponse(null, false)); return; }
            string content = await File.ReadAllTextAsync(logPath);
            // Syringe dumps registers on a crash, so the tail is the useful
            // part and the whole file may be large.
            int max = request.MaxBytes > 0 ? request.MaxBytes : 262144;
            if (content.Length > max) content = content[^max..];
            await WriteAsync(context, 200, new LaunchAgentLogResponse(content, true));
            return;
        }

        if (method == "POST" && path == LaunchAgentProtocol.ExecPath)
        {
            if (!allowExec) { await WriteAsync(context, 404, new { error = "diagnostic shell is not enabled; start the agent with --allow-exec" }); return; }
            LaunchAgentExecRequest request = await ReadAsync<LaunchAgentExecRequest>(context);
            if (string.IsNullOrWhiteSpace(request.Command)) { await WriteAsync(context, 400, new { error = "command required" }); return; }
            (string file, string prefixArgs) = request.Shell.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                ? ("cmd.exe", "/c")
                : ("powershell.exe", "-NoProfile -NonInteractive -Command");
            ProcessStartInfo startInfo = new()
            {
                FileName = file,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string part in prefixArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)) startInfo.ArgumentList.Add(part);
            startInfo.ArgumentList.Add(request.Command);
            using Process shell = Process.Start(startInfo) ?? throw new InvalidOperationException("shell did not start");
            Task<string> stdout = shell.StandardOutput.ReadToEndAsync();
            Task<string> stderr = shell.StandardError.ReadToEndAsync();
            int timeout = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : 120;
            bool exited = shell.WaitForExit(TimeSpan.FromSeconds(timeout));
            if (!exited)
            {
                try { shell.Kill(entireProcessTree: true); } catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) { }
                await WriteAsync(context, 200, new LaunchAgentExecResponse(-1, await stdout, await stderr, true));
                return;
            }
            Console.WriteLine($"exec [{request.Shell}] {request.Command}");
            await WriteAsync(context, 200, new LaunchAgentExecResponse(shell.ExitCode, await stdout, await stderr, false));
            return;
        }

        if (method == "POST" && path == LaunchAgentProtocol.RunPath)
        {
            LaunchAgentRunRequest request = await ReadAsync<LaunchAgentRunRequest>(context);
            if (!File.Exists(request.GameExecutable)) { await WriteAsync(context, 404, new { error = "game executable not found" }); return; }
            SpawnConfiguration configuration = new(
                request.GameExecutable,
                "agent",
                "agent",
                "0.0.0.0",
                0,
                "multiplayer",
                request.SpawnerExecutable,
                request.Arguments);
            Process process = await new WindowsSpawnerBoundary().StartAsync(configuration, request.WorkingDirectory, request.SpawnIniPath, CancellationToken.None);
            string runId = Guid.NewGuid().ToString("N");
            lock (runsGate) runs[runId] = new RunState(process);
            Console.WriteLine($"started run {runId}: {request.SpawnerExecutable ?? request.GameExecutable}");
            await WriteAsync(context, 200, new LaunchAgentRunResponse(runId));
            return;
        }

        if (method == "GET" && path.StartsWith(LaunchAgentProtocol.RunPath + "/", StringComparison.Ordinal))
        {
            string runId = path[(LaunchAgentProtocol.RunPath.Length + 1)..];
            RunState? state;
            lock (runsGate) runs.TryGetValue(runId, out state);
            if (state is null) { await WriteAsync(context, 404, new { error = "unknown run" }); return; }
            await WriteAsync(context, 200, state.ToStatus());
            return;
        }

        await WriteAsync(context, 404, new { error = "unsupported", path });
    }
    catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    {
        try { await WriteAsync(context, 500, new { error = exception.GetType().Name, message = exception.Message }); }
        catch (HttpListenerException) { /* client already gone */ }
    }
}

async Task<T> ReadAsync<T>(HttpListenerContext context)
{
    using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
    string body = await reader.ReadToEndAsync();
    return JsonSerializer.Deserialize<T>(body, json) ?? throw new JsonException($"empty {typeof(T).Name}");
}

async Task WriteAsync(HttpListenerContext context, int status, object payload)
{
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, json);
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json";
    context.Response.ContentLength64 = bytes.Length;
    await context.Response.OutputStream.WriteAsync(bytes);
    context.Response.Close();
}

internal sealed class RunState(Process process)
{
    public LaunchAgentRunStatus ToStatus()
    {
        if (!process.HasExited) return new LaunchAgentRunStatus(LaunchAgentRunStatus.Running, null, null);
        return new LaunchAgentRunStatus(LaunchAgentRunStatus.Exited, process.ExitCode, null);
    }
}
