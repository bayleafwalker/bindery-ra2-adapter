# Live gate attempt — 2026-08-24

This is an environment record, not a qualification claim. Global
qualification remains false.

## Checks performed

The operator Steam source exists at:

```text
/games/SteamLibrary/steamapps/common/Command & Conquer Red Alert II/
```

The two game executables are present and were read without copying them:

```text
gamemd.exe  sha256:3e81a61775d2745d1dabe397325ef663cd994ffc194da4e998e3bf5d2d308600
RA2MD.exe   sha256:a1fa31ca184db8d7c59ac9e300076a27a02495591e60753e99c58b3d1aad4e65
```

The golden runtime is incomplete on this host. These required files are not
present in the Steam tree:

```text
Syringe.exe
CnCNet-Spawner.dll
libra2yrcpp.dll
ra2yrcpp.json
```

The local Docker runtime contains only the `kind-bindery` Kubernetes node.
The cluster contains the historical `bindery-demo` sample workloads and no
external-runtime API, relay, or CnCNet tunnel service.

The accepted `feat/external-runtime-w0-w1` core branch contains the v1
external-runtime reference service and generic allocator seam, but its
`cmd/bindery-external-runtime` entry point calls `NewService()` without a
placement allocator. No served `cncnet-private` placement is therefore
available from the current environment.

The gate was then advanced in an isolated working copy of that accepted core
branch. A reviewed environment allocator now serves only
`cncnet-private`, and a loopback CnCNet .NET tunnel was started from the pinned
image digest recorded in `UPSTREAM-REVISIONS.yaml`. The source Steam tree was
staged into disposable `/tmp` appliance clones with the reviewed package,
spawner, and ra2yrcpp artifacts; this staging is not a repository asset or a
replacement for the canonical Windows VM.

## Gate result

| Gate | Result | Evidence |
| --- | --- | --- |
| Steam/YR source | Partial | Both executables present; runtime packages absent |
| Golden appliance manifest | Blocked | Syringe, spawner, instrumentation absent |
| Two distinct Windows clients | Not run | No Windows VM/appliance clones available |
| Control-plane session | Blocked | No deployed external-runtime service |
| `cncnet-private` placement | Blocked | No allocator-backed deployment |
| Private tunnel traffic | Not run | No tunnel workload |
| ra2yrcpp telemetry | Not run | Instrumentation absent |
| Kctl intake authority | Open | Existing authority blocker remains |
| Oracle read tracing | Open | Existing tracing blocker remains |
| Human acceptance | Open | No live run to review |
| Global qualification | False | Correct fail-closed result |

## Advanced gate result

The following checks were completed after the initial inventory:

| Gate | Result | Evidence |
| --- | --- | --- |
| Pinned private tunnel process | Passed | Loopback-only TCP/UDP ports 50000/50001; master announcement disabled |
| VM-reachable tunnel endpoint | Pending | Must bind `192.168.122.1` for the running libvirt guest network |
| Golden manifest generation | Passed (disposable staging) | `ra2-yr-cncnet-v0.1`; `gamemd.exe` hash matched both clones |
| Coordinator allocator | Passed | Explicit `cncnet-private`, `eu-north`, `127.0.0.1:50001` placement |
| Two-client enrollment | Passed | Two distinct account/client IDs; same placement; session remained `admitting` |
| Enrollment heartbeat | Passed | Authenticated client lease heartbeat accepted |
| Real game processes | Not run | Requires two Windows VM clones and display/game execution |
| Tunnel packet traffic | Not observed | No real game processes were attached |
| ra2yrcpp raw events | Not observed | No real game processes were attached |
| Kctl intake authority | Open | Still requires the served authority decision |
| Oracle read tracing | Open | Still requires traced oracle reads |
| Human acceptance | Open | No real match was available to review |

The staged launch plumbing also received a bounded Wine smoke test. Syringe
successfully recognized the injected DLL set and its log recorded
`Running process ... gamemd.exe -SPAWN -CD -LOG` after the adapter corrected
the `--args=` contract. The process did not produce a match, tunnel traffic,
or telemetry evidence under this Linux/Xvfb environment, so this is only a
launcher regression check and is not Windows acceptance evidence.

## Required unblock sequence

1. Build one canonical Windows golden VM from the legitimate Steam source,
   install the pinned CnCNet YR package, Syringe, `yrpp-spawner`, and the
   Bindery fork of ra2yrcpp, then generate and retain the private appliance
   manifest. Disposable host staging is not sufficient for this gate.
2. Deploy the external-runtime service with the explicit allocator change from
   the isolated core handoff, and deploy the maintained CnCNet .NET tunnel with
   master announcement disabled.
3. Clone the golden VM into two isolated clients with distinct machine,
   account, and telemetry identities.
4. Run the Windows live harness and attach tunnel packet counters and raw
   ra2yrcpp event counts.
5. Resolve the served Kctl `knowledge.candidate.intake` authority and attach
   traced oracle reads and human acceptance before submitting the packet.
