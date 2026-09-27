# RA2 channel: room stream and optional Twitch output

The channel's broadcast path is one OBS production, published once:

```text
rendered client window --(OBS capture)--> OBS --RTMP--> MediaMTX path "ra2"
                                                          |-- WebRTC / HLS / RTSP --> room
                                                          '-- twitch-relay.sh --> Twitch (only when enabled)
```

`ChannelRunner` drives OBS through its built-in obs-websocket server
(`ObsWebSocketProduction`): it selects the holding and match scenes and starts
or stops the stream output. It never touches MediaMTX. Public output is a
MediaMTX-side switch, so turning Twitch on or off cannot disturb the capture or
the room stream.

## OBS

1. Enable **Tools → WebSocket Server Settings**, with authentication. Keep the
   port on the lab network.
2. Create two scenes: `ra2-match` (a window or game capture of the selected
   client, plus desktop/application audio) and `ra2-holding` (a still or loop
   shown between matches). Other names can be set in `BroadcastScenes`. For one
   scene per client, pass `matchSceneByClient` to `ObsWebSocketProduction`.
3. **Settings → Stream**: service *Custom*, server `rtmp://<mediamtx-host>:1935`,
   stream key `ra2`.

The capture must be of a rendered client. The ra2yrcpp stream is game data for
the agent, the match record and overlays; it is not video.

## MediaMTX

```bash
install -m 0755 twitch-relay.sh /opt/ra2-channel/twitch-relay.sh
TWITCH_STREAM_KEY=... mediamtx /path/to/deploy/ra2-channel/mediamtx.yml
```

`ffmpeg` must be on the MediaMTX host's `PATH`. The Twitch key is read only from
the `TWITCH_STREAM_KEY` environment variable of the MediaMTX process.

## Public output

```bash
mkdir -p /run/ra2-channel
touch /run/ra2-channel/publish-public   # start relaying to Twitch
rm /run/ra2-channel/publish-public      # stop; the room stream continues
```

The flag is checked every five seconds while OBS is publishing. Set
`BroadcastPlan.PublishPublicly` to match, so each `ChannelMatchRecord` says
whether that match went out publicly.

## Rollback

Remove the flag file to stop public output. Use `NoBroadcastProduction` to run
the channel loop without OBS at all. The existing private match path —
`LiveAcceptanceRunner` and the `cncnet-private` tunnel — is unchanged either way.
