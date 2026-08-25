# Bindery RA2/YR adapter

GPL-3.0-or-later Windows adapter boundary for the Bindery external-runtime
research experiment. The repository contains client-owned control-plane and
launch plumbing only. It does not contain Red Alert 2/Yuri's Revenge
executables, maps, registry exports, proprietary assets, or redistributed game
files.

The adapter supports three explicit transport profiles:

- `bindery-native`: the `bindery-relay/v1` opaque relay contract;
- `cncnet-private`: the live hermetic CnCNet-compatible tunnel boundary;
- `cncnet-baseline`: an unchanged CnCNet tunnel deployment retained only for
  comparison.

The live target is Yuri's Revenge in CnCNet RA2 Mode. The spawner/injection
boundary is represented by `ISpawnerBoundary` and models Syringe loading
`yrpp-spawner`; the adapter never copies or embeds proprietary game code. A
Windows CI runner must perform the real spawner build and field acceptance
after the upstream notices and revisions are reviewed.

The control-plane client uses the v1 session shape (`compatibility`,
`participant_policy`, `placement`, and `capture`) and emits lifecycle kinds as
the wire strings `ready`, `started`, `exited`, `failed`, and
`capture_degraded`. Relay placement and transport credentials remain
coordinator-issued; the adapter does not select or mint them.

Reports return typed session/enrollment state, and known session or enrollment
IDs can be read through the public v1 endpoints. These reads expose observed
control-plane phases; they are not synthetic match evidence.

`TwoClientMatchDriver` prepares the native path for two distinct player
identities and remains the local relay fixture. `CncNetPrivateMatchDriver`
prepares the live path: it creates one session, enrolls both clients, requires a
coordinator-issued `cncnet-private` placement, and hands the endpoint to the
external CnCNet-compatible tunnel boundary. It intentionally does not emulate
that tunnel protocol.

The two live clients should be disposable clones of one golden Windows
appliance. See [`docs/golden-appliance.md`](docs/golden-appliance.md) and the
lab boundary in [`docs/architecture/ra2-yr-cncnet-lab.md`](docs/architecture/ra2-yr-cncnet-lab.md).
The generated golden manifest is schema v2 and records the exact selected
package-embedded spawner artifact; the standalone spawner release is not
silently interchangeable.

## Local checks

```powershell
dotnet build src/Bindery.Ra2.Adapter/Bindery.Ra2.Adapter.csproj --configuration Release
dotnet test tests/Bindery.Ra2.Adapter.Tests/Bindery.Ra2.Adapter.Tests.csproj --configuration Release
dotnet build tools/Bindery.Ra2.Adapter.LiveAcceptance/Bindery.Ra2.Adapter.LiveAcceptance.csproj --configuration Release
dotnet build tools/Bindery.Ra2.Adapter.ControlPlaneGate/Bindery.Ra2.Adapter.ControlPlaneGate.csproj --configuration Release
```

The control-plane-only gate can exercise the real identity, placement,
enrollment, readiness-boundary, and heartbeat path without claiming a game
match:

```bash
dotnet run --project tools/Bindery.Ra2.Adapter.ControlPlaneGate/Bindery.Ra2.Adapter.ControlPlaneGate.csproj -- \
  /private/ra2-yr-cncnet-v0.2.manifest.json http://127.0.0.1:18080
```

The live Windows handoff is documented in
[`docs/live-qualification.md`](docs/live-qualification.md). It requires a
private settings file and a real Windows/control-plane environment; no live
credentials or proprietary game files belong in this repository.
