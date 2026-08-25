// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bindery.Ra2.Adapter;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("usage: Bindery.Ra2.Adapter.ApplianceManifest <install-path> <output-path> [appliance-id]");
    return 2;
}

try
{
    string installPath = Path.GetFullPath(args[0]);
    string outputPath = Path.GetFullPath(args[1]);
    string applianceId = args.Length == 3 ? args[2] : "ra2-yr-cncnet-v0.2";
    if (!Directory.Exists(installPath)) throw new DirectoryNotFoundException(installPath);
    ArgumentException.ThrowIfNullOrWhiteSpace(applianceId);

    string[] required = [
        "gamemd.exe",
        "RA2MD.exe",
        "Syringe.exe",
        "CnCNet-Spawner.dll",
        "libra2yrcpp.dll",
        "zlib1.dll",
        "ra2yrcpp.json",
    ];
    List<GoldenApplianceArtifact> artifacts = [];
    foreach (string relativePath in required)
    {
        string path = Path.Combine(installPath, relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("required golden appliance artifact was not found", path);
        FileInfo file = new(path);
        artifacts.Add(new GoldenApplianceArtifact(relativePath, Hashing.Sha256File(path), file.Length));
    }
    GoldenApplianceArtifact spawner = artifacts.Single(static artifact =>
        string.Equals(artifact.RelativePath, "CnCNet-Spawner.dll", StringComparison.OrdinalIgnoreCase));

    GoldenApplianceManifest manifest = new(
        Ra2LabProfile.GoldenApplianceSchemaVersion,
        applianceId,
        installPath,
        "steam-owned-local-assets-plus-pinned-open-runtime",
        Ra2LabProfile.GameFamily,
        Ra2LabProfile.GameVersion,
        Ra2LabProfile.TransportProviderId,
        artifacts,
        "clone the golden VM, then assign unique hostname, NIC identity, Bindery identity, client instance id, and per-match SPAWN.INI",
        new GoldenApplianceProvenance(
            "https://github.com/CnCNet/cncnet-yr-client-package",
            "yr-9.3.2",
            "8ac409fc4e9f820b404d4ee7adf5f13d7a41b857",
            "package_9.3.2.zip",
            spawner.RelativePath,
            spawner.Sha256,
            spawner.Bytes,
            "Use the official package-embedded CnCNetYR build; retain standalone yrpp-spawner v0.0.0.16 only as a non-selected provenance reference."));
    string? parent = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
    JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(manifest, options));
    Console.WriteLine($"manifest={outputPath}");
    Console.WriteLine($"appliance_id={applianceId}");
    Console.WriteLine($"gamemd_sha256={artifacts[0].Sha256}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"manifest generation failed: {exception.Message}");
    return 1;
}
