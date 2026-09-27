#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Forward the room stream to Twitch while public output is enabled.
#
# MediaMTX starts this when OBS begins publishing and stops it when OBS
# stops. Public output is enabled by the presence of $RA2_CHANNEL_PUBLIC_FLAG
# (default /run/ra2-channel/publish-public) and the stream key is read from
# the TWITCH_STREAM_KEY environment variable -- never from this file or the
# MediaMTX config.
set -eu

flag="${RA2_CHANNEL_PUBLIC_FLAG:-/run/ra2-channel/publish-public}"
ingest="${TWITCH_INGEST:-rtmp://live.twitch.tv/app}"
source="rtsp://127.0.0.1:${RTSP_PORT:-8554}/${MTX_PATH:-ra2}"
relay=""

stop_relay() {
    if [ -n "$relay" ]; then
        kill "$relay" 2>/dev/null || true
        wait "$relay" 2>/dev/null || true
        relay=""
    fi
}
trap 'stop_relay; exit 0' INT TERM

while :; do
    if [ -e "$flag" ]; then
        if [ -z "${TWITCH_STREAM_KEY:-}" ]; then
            echo "twitch-relay: public output requested but TWITCH_STREAM_KEY is not set" >&2
        elif [ -z "$relay" ] || ! kill -0 "$relay" 2>/dev/null; then
            ffmpeg -nostdin -loglevel warning -rtsp_transport tcp -i "$source" \
                -c copy -f flv "$ingest/$TWITCH_STREAM_KEY" &
            relay=$!
        fi
    else
        stop_relay
    fi
    sleep 5
done
