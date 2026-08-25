# Golden RA2/YR appliance

Use one known-good Windows VM as the source of truth, then clone it for the
two clients. Do not hand-maintain two game installations.

The current operator-owned source installation is:

```text
/games/SteamLibrary/steamapps/common/Command & Conquer Red Alert II/
```

That path is local input only. The repository does not copy, scan into source
control, or redistribute the Steam game tree. The checked-in
`ci/inspect-ra2-appliance.ps1` script writes a private manifest containing
hashes for the game executables, Syringe, the CnCNet spawner, and the
instrumentation DLL.

Prepare the golden VM in this order:

1. Install the legitimately owned Steam YR assets.
2. Pin the official CnCNet YR package, using its package-embedded
   `CnCNet-Spawner.dll` (`401920` bytes, `sha256:9a49a621...`) as the selected
   runtime artifact. The standalone `yrpp-spawner` v0.0.0.16 DLL is retained
   only as a provenance reference and must not replace the package binary.
   Pin Syringe and the Bindery instrumentation build from
   `UPSTREAM-REVISIONS.yaml`.
3. Add `ra2yrcpp.json` with the listener policy required by the VM network. For
   the current `qemu:///session`/`virbr0` lab, the host gateway is
   `192.168.122.1`, so the allowlist must include it:

   ```json
   { "port": 14521, "allowedHostsRegex": "127\\.0\\.0\\.1|172\\..+|192\\.168\\.122\\.1", "logFilename": "ra2yrcpp.log" }
   ```

   Do not widen this to `0.0.0.0` unless the network isolation design is
   separately reviewed.
4. Capture a clean snapshot such as `bindery-ra2-v0.2`.
5. Clone that snapshot for `client-a` and `client-b`.

The clones must diverge in hostname, NIC/MAC identity, Bindery account token,
client instance ID, telemetry instance ID, and generated per-match
`SPAWN.INI`. Their game binaries, maps, rules, CnCNet package, spawner, and
instrumentation should remain byte-identical.

Create the private manifest with the cross-platform tool (run it from the host
that owns the staged appliance):

```bash
dotnet run --project tools/Bindery.Ra2.Adapter.ApplianceManifest/Bindery.Ra2.Adapter.ApplianceManifest.csproj -- \
  'C:/Bindery/appliances/ra2-yr-cncnet-v0.2' \
  'C:/private/ra2-yr-cncnet-v0.2.manifest.json'
```

The PowerShell script in `ci/inspect-ra2-appliance.ps1` is retained for
Windows-only operators who want a script-native equivalent.

The generated manifest is schema `bindery.ra2.golden-appliance/v2` and records
the selected package spawner’s source release, commit, path, hash, and size.
The live settings then point `firstLaunch` and `secondLaunch` at the two
cloned runtime directories. The launcher writes the generated evidence INI,
installs it temporarily as `SPAWN.INI` in each clone, and restores any prior
file after the process exits.

The acceptance harness reaches the second clone through the launch agent
described in [`docs/live-qualification.md`](live-qualification.md), which is
what allows the two clients to be two machines while the orchestrator stays a
single process.

Do not run both clones with the same Windows machine identity or network
identity. Disposable isolated VMs make this straightforward; enterprise
Sysprep is not a requirement for the lab.
