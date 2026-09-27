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
| Channel loop | Holding scene, match, record, next match or drain | `ChannelRunner`, `LiveAcceptanceMatchLauncher` |
| Agent controller | A player seat: filtered observations in, commands out, a decision trace | `AgentSeat`, `PlayerObservationFilter`, `IPlayerController` |

The key boundary: **the direct stream is game data, not the video feed.** It
feeds the agent, the match record and any overlay. The broadcast always comes
from capturing a rendered participant or observer window.

## How a session runs

1. A `ChannelRequest` names the map, the capture source, the broadcast plan,
   the match budget and, optionally, the house an agent controls.
2. `ChannelRunner` starts the OBS output and shows the holding scene.
3. For each match, `IChannelMatchLauncher` runs the existing private path:
   `LiveAcceptanceMatchLauncher` calls `LiveAcceptanceRunner` with fresh
   idempotency keys, so Bindery creates the session, receives the
   `cncnet-private` placement and launches both cloned clients. The channel
   cuts to the match scene as the launch begins.
4. When the clients exit, the channel shows the holding scene, derives a
   `ChannelMatchRecord` from the live evidence and appends it to
   `channel-matches.ndjson`.
5. The channel starts the next match after the holding interval. It stops and
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
and whether the match went out publicly.

`Completed` requires the live runner's lifecycle-complete flag, no client
failure and no desync dump. Anything short of that is `Incomplete`; a match
that could not be prepared is `Failed`. `Winner` stays null unless the
telemetry names one. Exit codes cannot, because Yuri's Revenge exits the same
way for both sides. A channel record never upgrades the qualification flags
in the evidence it points to.

### Continuity rules

- A failed scene switch is recorded in `BroadcastIssues`, and the match keeps
  running. Broadcasting is a side effect of the loop, not a dependency.
- The output is stopped in a `finally` block, including on cancellation.
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
neutral channel. `IsSpectator` is already rendered into the spawn INI, but the
proven two-client run has not established the observer-to-video path. Treat
`ClientClass.Observer` capture as unproven until step 3 below.

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

**Not implemented:** the game-side command channel. `IPlayerCommandSink` is the
boundary a per-player ra2yrcpp command path would implement. Nothing in this
repository can issue orders into the game yet.

## Build order

1. **Prove the channel.** Run `ChannelRunner` with `LiveAcceptanceMatchLauncher`
   and `ObsWebSocketProduction` over the existing two-client match. Capture one
   player's view to the room stream for several consecutive games.
2. **Automate continuity.** The loop, the holding scene, drain, failure
   back-off and output release are in place. Still to do in the lab: detecting
   match end from telemetry before process exit, audio checks, and verifying
   that resources are released on each clone.
3. **Add the observer.** Show that a third client joins reliably and gives the
   view you want. This needs a three-client live request; `LiveAcceptanceRunner`
   drives two today.
4. **Add agent play.** Put one controller in a player seat with filtered
   observations and a recorded trace. The seat and trace are in place. This step
   needs the command sink and bridge-side ownership and visibility fields.
5. **Expand games.** Reuse the channel loop, broadcast and record. Give each new
   game its own launcher, observation filter and command sink.

## Rollback

Remove the public flag file to stop publishing. Use `NoBroadcastProduction` to
stop capture. Leave `AgentSeatHouse` unset and run no `AgentSeat` to disable the
agent. The private RA2 match path (`LiveAcceptanceRunner`, the
`cncnet-private` tunnel and the golden clones) is unchanged by all of this. The
only change to it is the optional seed pin and the seed and map recorded in its
evidence.
