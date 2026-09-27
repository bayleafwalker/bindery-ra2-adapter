# On-demand RA2 channel

The channel is built on the RA2 runtime Bindery already runs. Bindery starts
and tracks matches. The instrumented clients provide game-state evidence. A
rendered client provides the picture and sound. You can watch it locally while
working, publish the same production to Twitch when you want, and add AI
players without rebuilding the match infrastructure.

| Part | Responsibility | In this repository |
| --- | --- | --- |
| Bindery | Session, client allocation and placement, private transport, lifecycle and evidence | Existing: `CncNetPrivateMatchDriver`, `LiveAcceptanceRunner` |
| RA2/YR clients | The simulation: two players now, an optional observer later | External: golden-appliance clones |
| Direct-stream adapter | Instrumented state and events for analysis, overlays and agent observations | Existing seam: `IRa2TelemetrySource` |
| Capture and broadcast | One rendered client's video and audio, sent to the room stream and optionally Twitch | `IBroadcastProduction`, `ObsWebSocketProduction`, [`deploy/ra2-channel`](../../deploy/ra2-channel/README.md) |
| Channel loop | Holding scene, match, record, next match or drain | `ChannelRunner`, `LiveAcceptanceMatchLauncher`, `tools/Bindery.Ra2.Adapter.Channel` |
| Match telemetry | Match end and winner from the instrumented stream, shared with the agent | `MatchTelemetryTracker`, `TelemetryFanOut` |
| Agent controller | A player seat: filtered observations in, commands out, a decision trace | `AgentSeat`, `PlayerObservationFilter`, `IPlayerController` |

The key boundary: **the direct stream is game data, not the video feed.** It
feeds the agent, the match record and any overlay. The broadcast always comes
from capturing a rendered participant or observer window.

## How a session runs

1. A `ChannelRequest` names the map, the capture source, the broadcast plan,
   the match budget and, optionally, the house an agent controls.
2. `ChannelRunner` runs the OBS preflight (scenes exist, required audio
   inputs present and unmuted), starts the output and shows the holding scene.
3. For each match, `IChannelMatchLauncher` runs the existing private path.
   `LiveAcceptanceMatchLauncher` calls `LiveAcceptanceRunner` with fresh
   idempotency keys and its own evidence folder. Bindery creates the session,
   issues the `cncnet-private` placement and launches both cloned clients,
   plus the observer if one is configured. The channel cuts to the match
   scene when the **captured** client reports `started`, so session setup
   and loading stay on the holding scene.
4. With a telemetry source attached, one reader feeds both the match tracker
   and the agent seat. The tracker records match end and the winner.
5. When the clients exit, the launcher waits briefly for the final events
   (5 s by default), then closes the stream. The channel shows the holding
   scene, derives a `ChannelMatchRecord` from the live evidence and the
   tracker, and appends it to `channel-matches.ndjson`.
6. The channel starts the next match after the holding interval. It stops and
   releases the output when the budget is spent, a drain is requested
   (`RequestDrain`), two matches in a row fail, or it is cancelled.

The room stream is the primary playback path. MediaMTX receives OBS's single
RTMP publish and serves it over WebRTC, HLS and RTSP. Twitch receives a copy
relayed from MediaMTX while its flag file exists.

### What a match record holds

The channel ID and match index; the Bindery run and session IDs; the map and
**seed** (`LiveAcceptanceRequest.Seed` can pin it; otherwise the drawn seed is
now written to the live evidence); the golden appliance and distinct client
executable hashes; the adapter version; the outcome; the evidence, replay and
decision-trace paths; the number of playbook revisions; the capture source;
whether the match went out publicly; and, when telemetry was attached, the
event count, whether the stream reached `ra2.match.ended`, and any telemetry
failure.

`Completed` requires the live runner's lifecycle-complete flag, no client
failure and no desync dump. Anything short of that is `Incomplete`; a match
that could not be prepared is `Failed`. `Winner` stays null unless the
telemetry names one: the `winner` field of `ra2.match.ended`, or, after the
match ends, the single joined house that was never defeated. Exit codes
cannot name a winner, because Yuri's Revenge exits the same way for both
sides. A telemetry, tracker or agent failure is recorded on the match and
never turns a played match into a failed one. A channel record never upgrades the qualification flags
in the evidence it points to.

### Continuity rules

- A failed scene switch is recorded in `BroadcastIssues`, and the match keeps
  running. Broadcasting is a side effect of the loop, not a dependency.
- The output is stopped in a `finally` block, including on cancellation.
- The first Ctrl+C in the channel tool drains after the current match. A
  second Ctrl+C cancels, and the output is still stopped.
- `StartStream` is skipped when OBS reports the output is already active, so
  the channel can restart after a crash without failing on OBS's
  `OutputRunning` response.

## Placement and scale

Run the game clients where the RA2 chain already works: the libvirt golden
clones behind `192.168.122.1`. The control services can stay in the cluster.
OBS runs next to the client it captures. MediaMTX can run on the host or in the
cluster. Use the GPU node only where rendering, encoding or local inference
benefits from it. An on-demand VPS becomes useful as a relay endpoint if the
home uplink becomes the limit.

Start with **one match on air**. Three matches means six player clients, up
to three observers, and a decision about which match gets the camera. Other
matches can run as unbroadcast experiments with `NoBroadcastProduction`.

The first broadcast captures one player's view (`CaptureSource` with
`ClientClass.Player`). A spectator client is the better eventual view for a
neutral channel. `LiveAcceptanceRequest.Observer` adds it as a third client:

- it enrolls as `observer`, and the session's participant policy admits one
  observer;
- it is validated against the same private tunnel placement;
- it receives its own tunnel port and a spawn INI with `IsSpectator=Yes`;
- it has no `[SpawnLocations]` entry and appears last in the global order;
- it is judged apart from the players, as Bindery Core judges it: a failed
  or undeparted observer sets `ObserverDegraded` in the evidence and
  `ObserverIssue` on the channel record, and the players' match stands.

The live-acceptance and channel tools read it from `observerIdentity`,
`observerClientInstanceId`, `observerLaunch` and `observerHost`. This is
built, but no lab run has proven it yet. In particular, the spawner's
handling of a spectator with no starting location is taken from the existing
two-client spectator support; it has not been observed with three peers. AI
houses are numbered after every human peer, the spectator included
(`Multi4` onward). Whether the engine expects that with a spectator present
is also unverified, so run the first observer matches without AI houses.

## AI play

An AI is **another player controller**. `AgentSeat` binds one
`IPlayerController` to one house, through a `PlayerObservationFilter` and an
`IPlayerCommandSink` for that same house. The observer has no sink and stays
read-only.

The ra2yrcpp stream is a spectator's view. Giving that view to a player
controller would turn a strategy test into clairvoyance, so the filter fails
closed. An observation passes only if it is one of the following:

- public lifecycle: match started or ended, player joined or defeated;
- owned by the seat's house (the `house` payload field by default);
- listed as visible to the house (`visible_to`).

Anything else is withheld and counted. Until the bridge emits per-house
visibility, the agent sees only its own house and public events. That is by
design.

The first useful loop is a planner that revises a playbook at meaningful
events (new threat, stalled economy, tech transition) while a deterministic
controller issues routine orders. A controller returns both in a
`ControllerStep`. Every playbook revision, sent command and rejected command is
appended to `decision-trace.ndjson` with the source event ID. The seat closes
at `ra2.match.ended`. The trace path and revision count go into the channel
record, so a dynamic playbook can be compared across matches with the same map
and seed.

Through the channel, set `ChannelRequest.AgentSeat` to an
`AgentSeatAssignment` (house, player client instance, agent
`ControllerDeclaration`) and pass `LiveChannelMatchOptions.AgentSeat` and
`Telemetry`. The launcher refuses:

- an assignment with no seat;
- a seat bound to a different house;
- an assignment on a client that is not a player of the match.

It then:

- declares the agent controller on that client's enrollment, so Bindery's
  public enrollment record says the seat was agent-driven;
- writes the trace into the match's evidence folder;
- once the clients exit, uploads the trace into that client's capture as
  `application/vnd.bindery.decision-trace.v1+ndjson`.

The trace's content hash goes into the channel record. A failed upload is
recorded on the match and leaves the trace on disk.

**Not implemented:** the game-side command channel. `IPlayerCommandSink` is the
boundary a per-player ra2yrcpp command path would implement. Nothing in this
repository can issue orders into the game yet.

## Build order

| Step | Built here | Still needs |
| --- | --- | --- |
| 1. Prove the channel | `tools/Bindery.Ra2.Adapter.Channel` runs `ChannelRunner` → `LiveAcceptanceMatchLauncher` → `LiveAcceptanceRunner`, with OBS and MediaMTX ([`docs/channel-settings.example.json`](../channel-settings.example.json)) | Several consecutive lab matches on the room stream |
| 2. Automate continuity | Cut on `started`, holding scene, drain, failure back-off, output release, OBS scene and audio preflight, per-match evidence folders | A lab check that each clone's resources are released; continuous audio-level monitoring |
| 3. Add the observer | Optional third client end to end: enrollment, tunnel port, spectator INI, lifecycle, evidence | A lab run proving the observer joins reliably and gives the view you want |
| 4. Add agent play | Seat, filter, trace, launcher wiring, winner from telemetry, controller declaration, trace upload, NDJSON telemetry recordings | The fork's live reader, bridge ownership and visibility fields, and the native command transport (see below) |
| 5. Expand games | Loop, broadcast and record are game-neutral behind `IChannelMatchLauncher` | Per-game launcher, filter and command sink |

## Prospective work outside this repository

These items are built against seams in this repository but need work
elsewhere. None of them is implemented here.

- **Live telemetry reader.** The ra2yrcpp fork owns protobuf/TCP framing.
  This repository reads **recordings**: `NdjsonTelemetrySource` follows a
  file of raw observations, one per line, as they are written
  (`NdjsonTelemetryFormat` defines the line). The channel tool's
  `telemetryRecording` setting points it at one file per match. Anything
  that decodes the live stream can write that file. A direct TCP reader
  belongs with the fork's decoder.
- **Ownership and visibility.** The filter and tracker read `house`,
  `visible_to` and `winner` from payloads. The bridge must emit these (or the
  field names must be configured to match what it emits). Per-house
  visibility needs the engine's own shroud and fog state; the full spectator
  view must never be relabelled as a player's view.
- **Command sink.** A per-player command path in
  [bayleafwalker/ra2yrcpp](https://github.com/bayleafwalker/ra2yrcpp),
  bound to one house and refusing orders for any other. The adapter side
  implements `IPlayerCommandSink`, and the observer gets none. The fork's
  `feat/bindery-player-command-boundary` branch rejects unit orders whose
  sources belong to another player. The native command transport and stable
  entity-ID mapping are still to come.
- **Camera for several matches.** With more than one match running, choose
  which one is on air. The loop plays one match at a time today; extra matches
  would be separate unbroadcast runners.
- **Remote relay.** If the home uplink limits Twitch output, move
  `twitch-relay.sh` to an on-demand VPS that pulls the room stream over a
  private link. The channel does not change.
- **Bindery Core.** Observers, the observer limit and capture objects already
  existed. The channel's enablers are in bayleafwalker/bindery-core#9:
  - an optional `controller` on player enrollment;
  - `GET /v1/objects/{content_hash}`;
  - the decision-trace media type.

  The decision note is `docs/decisions/ra2-channel.md` there.

## Rollback

Remove the public flag file to stop publishing. Use `NoBroadcastProduction` to
stop capture. Leave `AgentSeat` unset and pass no seat factory to disable the
agent. The private RA2 match path (`LiveAcceptanceRunner`, the
`cncnet-private` tunnel and the golden clones) is unchanged by all of this. The
only change to it is the optional seed pin and the seed and map recorded in its
evidence.
