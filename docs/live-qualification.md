# Live acceptance and global qualification handoff

The live target is two clients cloned from one golden Yuri's Revenge
appliance, CnCNet RA2 Mode, `yrpp-spawner` through Syringe, a private
`cncnet-private` tunnel allocation, and a local ra2yrcpp-derived telemetry
endpoint. Public CnCNet is not an acceptance environment for instrumented
clients.

Start with [`docs/golden-appliance.md`](golden-appliance.md), then confirm the
private tunnel deployment and control-plane placement provider are frozen as
one interface. The current `bindery-native` path is a test fixture and must not
be used to claim the live profile.

The tunnel deployment is separate from this Windows harness. Use the
maintained CnCNet .NET tunnel image at a pinned digest, bind its required TCP
and UDP ports, disable master announcement (`--no-master-announce`), and
record the allocation-to-endpoint mapping with the tunnel's packet counters.
For the current libvirt VM network, bind the host-side tunnel to
`192.168.122.1` rather than loopback and issue
`192.168.122.1:50001` from the coordinator. The VM `ra2yrcpp.json` allowlist
must include `192.168.122.1` as documented in `golden-appliance.md`. Do not
use `:latest` in the acceptance packet.

## Prepare the settings

Copy `docs/live-acceptance-settings.example.json` to an access-controlled
Windows path. Fill in two separate account tokens, two client instance IDs,
the golden appliance ID, the map and artifact hashes from the private
manifest, and the two cloned runtime directories. Set each launch to the
Syringe executable and leave its arguments as `-SPAWN`, `-CD`, and `-LOG`.

The harness expects `gamemd.exe` from the cloned YR tree as its game
executable. It invokes Syringe with that executable, and the spawner reads the
generated `SPAWN.INI` from the clone's working directory.

## Where each client runs

The two acceptance clients are two cloned guests, not two directories. The
orchestrator runs inside `client-a` and owns that client directly; `client-b`
is driven through the launch agent running inside it:

```text
client-a  192.168.122.189   orchestrator + local client
client-b  192.168.122.10    launch agent on TCP 14620
host      192.168.122.1     private CnCNet tunnel
```

Start the agent on `client-b` first, from an elevated prompt:

```powershell
powershell -ExecutionPolicy Bypass -File E:\start-agent.ps1
```

It listens only on the lab network, requires a bearer token shared with the
orchestrator, and exposes five fixed operations (health, validate, hash,
spawn-ini install/restore, run). It has no generic command endpoint.

Point the settings at it with `secondHost`:

```json
"secondHost": { "uri": "http://192.168.122.10:14620/", "tokenFile": "C:/private/agent-token.txt" }
```

Omit `firstHost`/`secondHost` to run a client on the orchestrator's own
machine. Omitting both is still supported for fixture runs, but it means the
two clients share one Windows and network identity, which the golden-appliance
requirement does not accept — the harness warns when that happens.

The executable hash for each client is taken on the machine that runs it. A
locally hashed copy would prove nothing about the other guest.

## Compatibility hashes

`gameHash` is the golden `gamemd.exe` hash from the private manifest.

`mapHash` is the sha256 of the map file actually staged into both clones. The
lab uses one official two-player map taken from the pinned CnCNet package
release asset (`package_9.3.2.zip`), not from the operator's Steam archives:
the stock `MAPS*.MIX` files are Blowfish-encrypted, and the package maps are
already covered by `UPSTREAM-REVISIONS.yaml`. The map is staged into each
clone's appliance directory; it is not one of the seven hashed manifest
artifacts, so staging it does not disturb the appliance match.

`modHash` had no upstream definition, so this is a stated one:

```text
modHash = sha256 over, for each file of the pinned CnCNet package subset
          sorted by lowercase filename, the line "<filename> <sha256hex>\n"
```

Filenames are included so a rename cannot pass unnoticed, and the sort makes
the value independent of directory order. Both clones run byte-identical
packages and therefore compute the same value.

## Run the live harness

```powershell
dotnet run --project tools/Bindery.Ra2.Adapter.LiveAcceptance/Bindery.Ra2.Adapter.LiveAcceptance.csproj --configuration Release -- C:/private/live-acceptance-settings.json
```

The harness creates one session, enrolls both players, requires a
coordinator-issued `cncnet-private` placement, writes per-client spawner
configuration, starts both processes, reports ready/started/exited lifecycle
events, reads final control-plane state, and writes
`live-acceptance-evidence.json`. It never writes credentials to evidence and
never changes global qualification.

The harness records the relay/tunnel endpoint and the ra2yrcpp telemetry
endpoint, but it does not fabricate packet counters or raw-event counts. Add
those from the actual private tunnel and telemetry agent in a separate,
access-controlled evidence file:

```json
{
  "relay_traffic_observed": true,
  "packets_forwarded": 1234,
  "telemetry_raw_events_observed": true,
  "telemetry_raw_event_count": 9876,
  "kctl_intake_authorized": true,
  "oracle_reads_traced": true,
  "match_observed_by": "<operator identity>",
  "match_observed_run_id": "<run_id from live-acceptance-evidence.json>"
}
```

## Spawner crashes do not show up as exit codes

Syringe is a debugger. It returns 0 after the game it hosted dies, so the
process exit code says nothing about whether the client actually ran. The
first live run recorded `exit 0`, `failure: null` and a complete lifecycle for
a run where both clients threw `0xE06D7363` and no relay traffic ever
happened.

Syringe is a debugger, so its log describes events, not verdicts, and three
different readings of it were wrong before this was settled:

- `SyringeDebugger::HandleException` is Syringe's ordinary log channel --
  feature-flag notes and hook setup arrive through it.
- `Exception (Code: ...)` lines are **first-chance** exceptions: ones the game
  throws and handles. RA2 with Ares logs several in a match that plays and
  exits cleanly.
- The hosted game's exit code is not a verdict either. Yuri's Revenge exits
  with **3** when the player quits from the score screen.

The harness therefore records what it saw as observations
(`spawner_exception`, `hosted_exit_code`, `desync`) rather than deciding from
log strings. Only a **notable** observation bears on qualification, and the
one that qualifies is a desync: the game writes `SYNC0.TXT` when the two
simulations diverge. A desync is not a crash -- both clients still exit
normally -- but a diverged match is not an acceptable one.

## Run the qualification preflight

```powershell
./ci/verify-live-qualification.ps1 `
  -EvidencePath C:/private/live-acceptance/live-acceptance-evidence.json `
  -ExternalEvidencePath C:/private/live-acceptance/external-evidence.json
```

### Human acceptance

There is no separate human-acceptance flag. Acceptance is an operator attesting
that they watched one specific match complete, so the packet records who
observed it and which run, and the preflight checks that the named run is this
run and that its clients actually completed. An observation naming a run whose
clients failed is a contradiction, not an acceptance.

The script fails closed unless both clients identify the same golden appliance
and executable hash, use distinct identities, report ready/started/exited,
exit successfully, reach ended/departed control-plane state, use
`cncnet-private`, and have positive tunnel traffic, Kctl authority, oracle
tracing, and human acceptance evidence.

Passing means “ready to submit to the global authority.” It does not set or
publish qualification. Keep the global state false until the authority accepts
the packet and the untraced oracle-read blocker is resolved.
