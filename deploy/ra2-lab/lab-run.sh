#!/usr/bin/env bash
# One host-side command for an unattended RA2 lab match.
#
#   lab-run.sh --preflight            check every precondition, start nothing
#   lab-run.sh --stage 1              LiveAcceptance: player-a vs 2 Brutal AI, player-b spectating
#   lab-run.sh --stage 2              Channel tool: player-a + agent seat player-b ($LAB_SEAT_ROUTINE)
#                                     vs 2 Brutal AI, live telemetry from client-a, fork ra2yrcpp
#   lab-run.sh --prepare-only --stage N   push payload, prepare guests, start the client-b agent; no match
#   lab-run.sh --dry-run --stage N     full run without the preflight gate (validates settings end to end)
#   lab-run.sh --teardown             stop agent/control plane/tunnel, restore stock ra2yrcpp
#
# Environment:
#   RA2_LAB_HOME    lab state directory: bin/bindery-external-runtime, payload/
#                   (build-payload.sh), secrets/agent-token.txt, runs/<id>/.
#                   Kept out of the repository: it holds game content and tokens.
#   RA2_LAB_TUNNEL  script that starts/stops the pinned private CnCNet tunnel
#                   container (`<script> up|down`).
#   PREPARE_STAGE   prepare the guests for another stage than the match, e.g.
#                   PREPARE_STAGE=2 --stage 1 plays a stage-1 match on the fork
#                   ra2yrcpp DLL (bisection).
#   LAB_MATCH_TIMEOUT  seconds before a match is stopped (default 2400).
#   LAB_DEFER_SERVICE_START  1: the fork starts its service on the first game
#                   frame (ra2yrcpp.json deferServiceStart); 0 (default): at ExeRun.
#   LAB_SEAT_ROUTINE   stage-2 agent seat routine: deploy_mcv (default) or
#                   build_order (deploy, then power, barracks, refinery).
#
# Host: pinned CnCNet tunnel (docker, 192.168.122.1:50000) and bindery-core
# external-runtime (native, 192.168.122.1:8080, state under runs/<id>/).
# Guests are driven only through the QEMU guest agent and the operator-made
# scheduled task \Bindery\BinderyLabRun (virtio, interactive, no triggers).
# Nothing is published off this machine. Tokens never go to stdout.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
LAB="${RA2_LAB_HOME:?set RA2_LAB_HOME to the lab state directory (see the header)}"
TUNNEL="${RA2_LAB_TUNNEL:?set RA2_LAB_TUNNEL to the private tunnel script (see the header)}"
EXEC="$HERE/ra2-vm-exec"
URI=${LIBVIRT_DEFAULT_URI:-qemu:///session}
TUNNEL_IMAGE=ghcr.io/cncnet/cncnet-docker-dotnetcore-tunnel-dotnet10@sha256:35c8eb8c44b51914b891ff5a44fc860d0efe5fef2d294b69ba07b92c12660447
BIND=192.168.122.1 CP_PORT=8080 RELAY=192.168.122.1:50000
AGENT_PORT=14620
# Guest addresses come from the guest agent, not from a DHCP lease someone wrote down.
guest_ip() { virsh -c "$URI" domifaddr "bindery-ra2-client-$1" --source agent 2>/dev/null | awk '$(NF-1) == "ipv4" && $NF !~ /^127\./ { sub("/.*", "", $NF); print $NF; exit }'; }
# Empty when a guest agent does not answer; preflight reports it and run modes refuse.
A_IP=$(guest_ip a || true) B_IP=$(guest_ip b || true)
CP_BIN="$LAB/bin/bindery-external-runtime"
PAYLOAD="$LAB/payload" SECRETS="$LAB/secrets"
MATCH_TIMEOUT=${LAB_MATCH_TIMEOUT:-2400}
DEFER_SERVICE_START=${LAB_DEFER_SERVICE_START:-0}
case "$DEFER_SERVICE_START" in 0|1) ;;
  *) echo "LAB_DEFER_SERVICE_START must be 0 or 1, not '$DEFER_SERVICE_START'" >&2; exit 2 ;; esac
SEAT_ROUTINE=${LAB_SEAT_ROUTINE:-deploy_mcv}
case "$SEAT_ROUTINE" in deploy_mcv|build_order) ;;
  *) echo "LAB_SEAT_ROUTINE must be deploy_mcv or build_order, not '$SEAT_ROUTINE'" >&2; exit 2 ;; esac

stage="" mode=run
while [ $# -gt 0 ]; do
  case "$1" in
    --stage) stage=$2; shift ;;
    --preflight) mode=preflight ;;
    --prepare-only) mode=prepare ;;
    --teardown) mode=teardown ;;
    # Skip the preflight gate and cap the wait: exercises the whole run even
    # while guest->8080 is closed (the harness then fails at the control plane).
    --dry-run) mode=dry; MATCH_TIMEOUT=240 ;;
    *) echo "usage: $0 --preflight | --stage 1|2 [--prepare-only | --dry-run] | --teardown" >&2; exit 2 ;;
  esac; shift
done

log() { printf '[%s] %s\n' "$(date +%T)" "$*"; }
gx() { local side=$1 timeout=${3:-120}; "$EXEC" "bindery-ra2-client-$side" "$2" "$timeout"; }
gpush() { local secret=(); [ "$1" = --secret ] && { secret=(--secret); shift; }; local side=$1; shift; "$HERE/ra2-vm-push" "${secret[@]}" "bindery-ra2-client-$side" "$@" >/dev/null; }
gpull() { local side=$1; shift; "$HERE/ra2-vm-pull" "bindery-ra2-client-$side" "$@"; }
# The agent token goes to curl as a header file, never on a command line (ps shows those).
auth() { printf 'Authorization: Bearer %s\n' "$(<"$SECRETS/agent-token.txt")"; }

# ---------------------------------------------------------------- preflight
preflight() {
  local miss=0
  ok()   { printf '  ok    %s\n' "$*"; }
  bad()  { printf '  MISS  %s\n' "$*"; miss=$((miss + 1)); }
  note() { printf '  note  %s\n' "$*"; }
  echo "host"
  docker info >/dev/null 2>&1 && ok "docker reachable" || bad "docker not reachable"
  docker image inspect "$TUNNEL_IMAGE" >/dev/null 2>&1 && ok "tunnel image present at pinned digest" \
    || bad "tunnel image missing: docker pull $TUNNEL_IMAGE"
  ip -o addr show dev virbr0 2>/dev/null | grep -q "$BIND/" && ok "virbr0 holds $BIND" || bad "virbr0 does not hold $BIND"
  [ -x "$CP_BIN" ] && ok "control plane binary $(basename "$CP_BIN")" || bad "control plane binary missing ($CP_BIN)"
  (cd "$PAYLOAD" && sha256sum -c --quiet SHA256SUMS) >/dev/null 2>&1 && ok "payload matches SHA256SUMS ($(wc -l <"$PAYLOAD/SHA256SUMS") files)" \
    || bad "payload missing or changed ($PAYLOAD)"
  [ -s "$SECRETS/agent-token.txt" ] && ok "agent token present" || bad "agent token missing ($SECRETS/agent-token.txt)"
  for p in $CP_PORT 50000; do
    if ss -Hlnt "sport = :$p" | grep -q .; then note "port $p already has a listener: $(ss -Hlntp "sport = :$p" | awk '{print $4, $6}' | head -1)"
    else ok "port $BIND:$p free"; fi
  done
  [ -n "$A_IP" ] && [ -n "$B_IP" ] && ok "guest addresses: client-a $A_IP, client-b $B_IP" || bad "guest agent reported no address (client-a '${A_IP}', client-b '${B_IP}')"
  for side in a b; do
    echo "client-$side"
    if ! virsh -c "$URI" domstate "bindery-ra2-client-$side" 2>/dev/null | grep -q running; then bad "domain not running"; continue; fi
    local out
    out=$(gx $side '
      $r = @()
      $t = Get-ScheduledTask -TaskPath "\Bindery\" -TaskName BinderyLabRun -EA SilentlyContinue
      if ($t -and $t.Principal.UserId -eq "virtio" -and "$($t.Principal.LogonType)" -eq "Interactive" -and -not $t.Triggers) { $r += "ok|BinderyLabRun task (virtio, interactive, no triggers)" } else { $r += "bad|BinderyLabRun task missing or not virtio/interactive/untriggered" }
      if (Get-Process explorer -IncludeUserName -EA SilentlyContinue | ? { $_.UserName -like "*\virtio" -and $_.SessionId -ge 1 }) { $r += "ok|virtio logged on interactively" } else { $r += "bad|virtio is not logged on (no interactive session for the game)" }
      $app = Get-ChildItem C:\Bindery\appliances -Directory | Select -First 1
      $gm = (Get-FileHash (Join-Path $app.FullName gamemd.exe)).Hash.ToLower()
      if ($gm -eq "3e81a61775d2745d1dabe397325ef663cd994ffc194da4e998e3bf5d2d308600") { $r += "ok|gamemd.exe matches golden hash ($($app.Name))" } else { $r += "bad|gamemd.exe hash $gm" }
      $dll = (Get-FileHash (Join-Path $app.FullName libra2yrcpp.dll)).Hash
      $r += "note|ra2yrcpp in tree: " + $(if ($dll.StartsWith("59B8D235")) { "stock v0.2" } else { "not stock ($($dll.Substring(0,12)); prepare restores it)" })
      $sm = (Get-FileHash (Join-Path $app.FullName spawnmap.ini)).Hash
      if ($sm.StartsWith("461A55939598")) { $r += "ok|spawnmap.ini = 4_cellular + Brutal AI" } else { $r += "note|spawnmap.ini will be staged" }
      $n = @(Get-ChildItem C:\Bindery\lab\bin\*.exe, C:\Bindery\lab\fork\*.dll -EA SilentlyContinue).Count
      $r += "note|payload in guest: $n of 6 binaries present (hash-checked and topped up at run)"
      if ((Get-MpPreference).ExclusionPath -contains "C:\Bindery\appliances") { $r += "ok|Defender exclusion for appliances" } else { $r += "bad|no Defender exclusion for C:\Bindery\appliances" }
      if ($env:COMPUTERNAME -like "*RA2-A") {
        if (Test-Path C:\private\ra2-yr-cncnet-v0.2.manifest.json) { $r += "ok|golden manifest C:\private\ra2-yr-cncnet-v0.2.manifest.json" } else { $r += "bad|golden manifest missing" }
      } else {
        if (Get-NetFirewallRule -DisplayName "Bindery RA2 launch agent" -EA SilentlyContinue | ? Enabled -eq True) { $r += "ok|firewall admits TCP 14620 from 192.168.122.0/24" } else { $r += "bad|no inbound rule for TCP 14620" }
        if (Get-Process Bindery.Ra2.Adapter.LaunchAgent -EA SilentlyContinue) { $r += "note|launch agent running" } else { $r += "note|launch agent not running (started by the run)" }
      }
      $r -join "`n"' 2>&1) || { bad "guest agent did not answer: $out"; continue; }
    while IFS='|' read -r kind text; do
      case "$kind" in ok) ok "$text" ;; bad) bad "$text" ;; *) note "$text" ;; esac
    done <<<"$(tr -d '\r' <<<"$out")"
  done
  echo "network"
  # Guest -> host:8080. With nothing listening a closed port and a firewall
  # drop look the same, so hold a short-lived probe listener (not the service).
  local probe="" cp_listening=0
  ss -Hlnt "sport = :$CP_PORT" | grep -q . && cp_listening=1
  if [ $cp_listening = 0 ]; then
    python3 -c "import socket,time; s=socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(('$BIND',$CP_PORT)); s.listen(8); time.sleep(40)" & probe=$!
    sleep 1
  fi
  local tnc
  tnc=$(gx a "(Test-NetConnection $BIND -Port $CP_PORT -WarningAction SilentlyContinue).TcpTestSucceeded" 60 2>&1 | tr -d '\r' | tail -1 || true)
  [ -n "$probe" ] && kill "$probe" 2>/dev/null
  [ "$tnc" = True ] && ok "client-a -> $BIND:$CP_PORT (Test-NetConnection True${probe:+, via probe listener})" \
    || bad "client-a -> $BIND:$CP_PORT blocked (Test-NetConnection $tnc${probe:+, probe listener was up}): host firewall on virbr0 must admit TCP $CP_PORT"
  if ss -Hlnt "sport = :50000" | grep -q .; then
    tnc=$(gx a "(Test-NetConnection $BIND -Port 50000 -WarningAction SilentlyContinue).TcpTestSucceeded" 60 2>&1 | tr -d '\r' | tail -1 || true)
    [ "$tnc" = True ] && ok "client-a -> tunnel $BIND:50000" || bad "client-a -> tunnel $BIND:50000 failed"
  else note "tunnel not up; client-a -> $BIND:50000 checked at run"; fi
  local code
  code=$(curl -s -m 5 -o /dev/null -w '%{http_code}' -H @<(auth 2>/dev/null) "http://$B_IP:$AGENT_PORT/health" || true)
  case "$code" in 200) ok "host -> client-b launch agent http://$B_IP:$AGENT_PORT/health 200" ;;
    000) note "client-b launch agent not answering (expected unless it is running)" ;;
    *) bad "client-b launch agent answered HTTP $code" ;; esac
  echo
  [ $miss = 0 ] && { echo "preflight: all required checks pass"; return 0; }
  echo "preflight: $miss required check(s) missing"; return 1
}

# ---------------------------------------------------------------- host services
tunnel_up() {
  "$TUNNEL" up >/dev/null || { log "tunnel did not start ($TUNNEL up failed)"; exit 1; }
  sleep 3
  local status; status=$(curl -s -m 5 "http://$BIND:50000/status" || true)
  [ -n "$status" ] || { log "tunnel started but $BIND:50000/status does not answer"; exit 1; }
  log "tunnel: ${status%%$'\n'*}"
}
cp_up() {
  local state="$RUN/control-plane"; mkdir -p "$state"
  if [ -f "$LAB/control-plane.pid" ] && kill -0 "$(cat "$LAB/control-plane.pid")" 2>/dev/null; then log "control plane already running"; return; fi
  BINDERY_EXTERNAL_RUNTIME_ADDR="$BIND:$CP_PORT" BINDERY_RELAY_PROVIDER=cncnet-private BINDERY_RELAY_ENDPOINT="$RELAY" \
  BINDERY_RELAY_REGION=eu-north BINDERY_PLACEMENT_POLICY_VERSION=cncnet-private-lab-v1 BINDERY_STATE_PATH="$state/state.json" \
    setsid "$CP_BIN" >"$state/control-plane.log" 2>&1 < /dev/null &
  echo $! > "$LAB/control-plane.pid"; sleep 1
  kill -0 "$(cat "$LAB/control-plane.pid")" || { cat "$state/control-plane.log"; exit 1; }
  log "control plane on $BIND:$CP_PORT (pid $(cat "$LAB/control-plane.pid"))"
}
cp_down() { [ -f "$LAB/control-plane.pid" ] && kill "$(cat "$LAB/control-plane.pid")" 2>/dev/null; rm -f "$LAB/control-plane.pid"; }

mint() {  # writes $RUN/secrets/identities.json (0600)
  mkdir -p "$RUN/secrets"; chmod 700 "$RUN/secrets"
  for side in a b; do
    curl -sf -X POST "http://$BIND:$CP_PORT/v1/identities" -H 'Content-Type: application/json' \
      -H "Idempotency-Key: $(cat /proc/sys/kernel/random/uuid)" \
      -d "{\"handle\":\"bindery-ra2-$side-$RUN_ID\",\"display_name\":\"Bindery RA2 $side\"}" > "$RUN/secrets/$side.json"
  done
  chmod 600 "$RUN/secrets/"*.json
  log "minted identities bindery-ra2-{a,b}-$RUN_ID"
}

render() {  # $RUN/secrets/settings.json for the stage
  python3 - "$RUN" "$RUN_ID" "$stage" "$(sha256sum "$PAYLOAD/spawnmap-brutal.ini" | cut -d' ' -f1)" "$B_IP" "$SEAT_ROUTINE" <<'PY'
import json, sys, pathlib
run, run_id, stage = pathlib.Path(sys.argv[1]), sys.argv[2], sys.argv[3]
b_ip, seat_routine = sys.argv[5], sys.argv[6]
ident = {s: json.loads((run / "secrets" / f"{s}.json").read_text()) for s in "ab"}
def identity(s): return {"accountId": ident[s]["public_identity"]["account_id"], "accountToken": ident[s]["account_token"]}
def launch(side, name, seat, spectator=False, command=None):
    d = {"gameExecutable": f"C:/Bindery/appliances/client-{side}/gamemd.exe",
         "workingDirectory": f"C:/Bindery/appliances/client-{side}",
         "mapId": "4_cellular.map", "playerName": name,
         "spawnerExecutable": f"C:/Bindery/appliances/client-{side}/Syringe.exe",
         "spawnerArguments": ["-SPAWN", "-CD", "-LOG"],
         "side": 0, "color": seat, "spawnLocation": seat, "isSpectator": spectator}
    if command: d["commandEndpoint"] = command
    return d
live = {
    "serviceUri": "http://192.168.122.1:8080",
    "firstIdentity": identity("a"), "secondIdentity": identity("b"),
    "adapterId": "bindery.ra2.yr-cncnet", "adapterVersion": "0.2.0",
    "gameFamily": "yuris-revenge", "gameVersion": "cncnet-ra2-mode", "modId": "cncnet-ra2-mode",
    "transportProvider": "cncnet-private", "goldenApplianceId": "ra2-yr-cncnet-v0.2",
    "goldenManifestPath": "C:/private/ra2-yr-cncnet-v0.2.manifest.json",
    "telemetryEndpoint": "127.0.0.1:14521", "telemetryProtocol": "ra2yrcpp-protobuf-tcp",
    # spawnmap.ini actually loaded: 4_cellular.map + INI/Game Options/AI/Brutal AI.ini appended
    "mapId": "4_cellular.map",
    "gameHash": "sha256:3e81a61775d2745d1dabe397325ef663cd994ffc194da4e998e3bf5d2d308600",
    "modHash": "sha256:4026595532c3b8fd84943f6c96c0c6f0281d79463e793ebee996a7d20de76da1",
    "mapHash": "sha256:" + sys.argv[4],
    "firstClientInstanceId": "client-a-decedec5", "secondClientInstanceId": "client-b-b703066a",
    "secondHost": {"uri": f"http://{b_ip}:14620/", "tokenFile": "C:/Bindery/lab/secrets/agent-token.txt"},
    "evidenceDirectory": f"C:/Bindery/lab/runs/{run_id}/evidence",
    "aiPlayers": [{"handicap": 2, "country": 2, "color": 2, "spawnLocation": 2, "team": 1},
                  {"handicap": 2, "country": 5, "color": 3, "spawnLocation": 3, "team": 1}],
    "region": "eu-north", "regionRttMilliseconds": 40, "latencyP95Milliseconds": 100,
}
if stage == "1":
    # The proven unattended shape: one human house (its defeat ends the match), one observer.
    live["firstLaunch"] = launch("a", "player-a", 0)
    live["secondLaunch"] = launch("b", "player-b", 1, spectator=True)
    settings = live
else:
    live["firstLaunch"] = launch("a", "player-a", 0)
    live["secondLaunch"] = launch("b", "player-b", 1, command=f"{b_ip}:14521")
    settings = {"channelId": "ra2-lab", "maximumMatches": 1, "holdingSeconds": 5,
                "captureClientInstanceId": "client-a-decedec5",
                "liveTelemetry": {"endpoint": "127.0.0.1:14521", "pollMilliseconds": 500},
                "agentSeat": {"house": "player-b", "clientInstanceId": "client-b-b703066a",
                              "routine": seat_routine, "controllerVersion": "0.1.0"},
                "live": live}
out = run / "secrets" / "settings.json"
out.write_text(json.dumps(settings, indent=2)); out.chmod(0o600)
PY
  log "rendered stage $stage settings (seat routine $SEAT_ROUTINE) (tokens only in $RUN/secrets, mode 0600)"
}

# ---------------------------------------------------------------- guests
push_payload() {
  for side in a b; do
    gx $side 'New-Item -ItemType Directory -Force C:\Bindery\lab\bin, C:\Bindery\lab\fork, C:\Bindery\lab\secrets, C:\Bindery\lab\runs | Out-Null
      & icacls C:\Bindery\lab\secrets /inheritance:r /grant:r "virtio:(OI)(CI)F" "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" | Out-Null' >/dev/null
    local have
    have=$(gx $side 'Get-ChildItem C:\Bindery\lab\bin\*.exe, C:\Bindery\lab\fork\*.dll, C:\Bindery\lab\spawnmap-brutal.ini -EA SilentlyContinue | % { (Get-FileHash $_.FullName).Hash.ToLower() }; exit 0' | tr -d '\r')
    while read -r sum file; do
      grep -q "$sum" <<<"$have" && continue
      case "$file" in fork/*) dest="C:\\Bindery\\lab\\fork\\$(basename "$file")" ;; *.ini) dest="C:\\Bindery\\lab\\$file" ;; *) dest="C:\\Bindery\\lab\\bin\\$file" ;; esac
      gpush $side "$PAYLOAD/$file" "$dest"; log "client-$side: pushed $file"
    done < "$PAYLOAD/SHA256SUMS"
    gpush $side "$HERE/guest/prepare.ps1" 'C:\Bindery\lab\prepare.ps1'
    gpush --secret $side "$SECRETS/agent-token.txt" 'C:\Bindery\lab\secrets\agent-token.txt'
  done
}
prepare_guests() {  # $1 = 1|2|restore
  for side in a b; do
    log "client-$side: prepare stage $1"
    local defer=""; [ "$1" = 2 ] && [ "$DEFER_SERVICE_START" = 1 ] && defer=" -DeferServiceStart"
    gx $side "& C:\\Bindery\\lab\\prepare.ps1 -Stage $1 -RunId '${RUN_ID:-}'${A_IP:+ -CommandPeer $A_IP}$defer" 180 | sed 's/^/    /'
  done
}
# Run a command in the guest's interactive session through BinderyLabRun.
labrun() {  # side, powershell body
  local tmp; tmp=$(mktemp); printf '%s\n' "$2" > "$tmp"
  gpush "$1" "$tmp" 'C:\Bindery\lab\run.ps1'; rm -f "$tmp"
  gx "$1" 'Start-ScheduledTask -TaskPath "\Bindery\" -TaskName BinderyLabRun' >/dev/null
}
agent_up() {
  if curl -s -m 3 -o /dev/null -w '%{http_code}' -H @<(auth) "http://$B_IP:$AGENT_PORT/health" | grep -q 200; then log "client-b agent already up"; return; fi
  labrun b '$lab = "C:\Bindery\lab"
"started $(Get-Date -Format o) session=$((Get-Process -Id $PID).SessionId) user=$env:USERNAME" | Set-Content "$lab\agent.started"
& "$lab\bin\Bindery.Ra2.Adapter.LaunchAgent.exe" "http://'"$B_IP"':14620/" "$lab\secrets\agent-token.txt" 2>&1 | Out-File -Encoding utf8 "$lab\agent.log"
"exit=$LASTEXITCODE $(Get-Date -Format o)" | Set-Content "$lab\agent.exited"'
  for _ in $(seq 30); do
    code=$(curl -s -m 3 -o "$LAB/.health" -w '%{http_code}' -H @<(auth) "http://$B_IP:$AGENT_PORT/health" || true)
    [ "$code" = 200 ] && { log "client-b agent healthy at http://$B_IP:$AGENT_PORT/ : $(cat "$LAB/.health")"; rm -f "$LAB/.health"; return; }
    sleep 2
  done
  gx b 'Get-Content C:\Bindery\lab\agent.started, C:\Bindery\lab\agent.log -EA SilentlyContinue' | tail -20
  echo "client-b agent did not come up" >&2; exit 1
}
agent_down() { gx b 'Get-Process Bindery.Ra2.Adapter.LaunchAgent -EA SilentlyContinue | Stop-Process -Force; "agent stopped"' || true; }

run_match() {
  local exe settings
  if [ "$stage" = 1 ]; then exe=Bindery.Ra2.Adapter.LiveAcceptance.exe settings=settings-stage1.json; else exe=Bindery.Ra2.Adapter.Channel.exe settings=settings-stage2.json; fi
  gpush --secret a "$RUN/secrets/settings.json" "C:\\Bindery\\lab\\secrets\\$settings"
  gx a "New-Item -ItemType Directory -Force C:\\Bindery\\lab\\runs\\$RUN_ID | Out-Null" >/dev/null
  labrun a "\$d = 'C:\\Bindery\\lab\\runs\\$RUN_ID'
\"started \$(Get-Date -Format o) session=\$((Get-Process -Id \$PID).SessionId) user=\$env:USERNAME\" | Set-Content \"\$d\\harness.started\"
& 'C:\\Bindery\\lab\\bin\\$exe' 'C:\\Bindery\\lab\\secrets\\$settings' 2>&1 | Out-File -Encoding utf8 \"\$d\\harness.log\"
\"\$LASTEXITCODE\" | Set-Content \"\$d\\harness.exit\""
  log "stage $stage harness started on client-a (run $RUN_ID); waiting up to ${MATCH_TIMEOUT}s"
  local t0=$SECONDS
  while :; do
    if gx a "Test-Path C:\\Bindery\\lab\\runs\\$RUN_ID\\harness.exit" | grep -q True; then break; fi
    if (( SECONDS - t0 > MATCH_TIMEOUT )); then log "timeout: stopping the match"; MATCH_RC=124; gx a 'Get-Process gamemd,Syringe,Bindery.Ra2.Adapter.* -EA SilentlyContinue | Stop-Process -Force' || true; gx b 'Get-Process gamemd,Syringe -EA SilentlyContinue | Stop-Process -Force' || true; sleep 5; break; fi
    sleep 15
  done
  local rc; rc=$(gx a "Get-Content C:\\Bindery\\lab\\runs\\$RUN_ID\\harness.exit -EA SilentlyContinue" | tr -d '\r' || true)
  log "harness finished after $((SECONDS - t0))s, exit ${rc:-none}"
  # The run's own exit status is the match's: an unattended caller must see a failed or stopped match.
  if [ "$MATCH_RC" = 0 ] && [ "$rc" != 0 ]; then MATCH_RC=1; fi
}

collect() {
  local out="$RUN/evidence"; mkdir -p "$out"
  "$HERE/relay-counters" finish "$RUN" | tee "$out/relay.txt"
  for side in a b; do
    gx $side "\$f = 'C:\\Bindery\\appliances\\client-$side\\ra2yrcpp.record'
      if (-not (Test-Path \$f)) { '0 0'; exit }
      \$in = [IO.File]::Open(\$f, 'Open', 'Read', 'ReadWrite'); \$gz = New-Object IO.Compression.GZipStream(\$in, [IO.Compression.CompressionMode]::Decompress)
      \$ms = New-Object IO.MemoryStream; try { \$gz.CopyTo(\$ms) } catch { }; \$gz.Dispose(); \$in.Dispose(); \$b = \$ms.ToArray(); \$i = 0; \$n = 0; \$tail = 0
      # A capture cut off when the game exits ends in a partial record: count the complete ones, report the tail.
      while (\$i -lt \$b.Length) { \$start = \$i; \$len = [long]0; \$shift = 0; \$ok = \$true
        do { if (\$i -ge \$b.Length) { \$ok = \$false; break }; \$c = \$b[\$i]; \$i++; \$len = \$len -bor ([long](\$c -band 0x7F) -shl \$shift); \$shift += 7 } while (\$c -band 0x80)
        if (-not \$ok -or \$i + \$len -gt \$b.Length) { \$tail = \$b.Length - \$start; break }; \$i += \$len; \$n++ }
      \"\$n \$(\$b.Length) \$tail\"" 180 | tr -d '\r' | tail -1 | awk -v s=$side '{print "client-" s ": " $1 " telemetry records (" $2 " bytes decompressed, " $3+0 " bytes partial tail)"}' | tee -a "$out/telemetry.txt"
  done
  # Copy the run folder (harness log, evidence, channel records, decision trace) back to the host.
  gx a "Compress-Archive -Force -Path C:\\Bindery\\lab\\runs\\$RUN_ID\\* -DestinationPath C:\\Bindery\\lab\\runs\\$RUN_ID.zip; (Get-Item C:\\Bindery\\lab\\runs\\$RUN_ID.zip).Length" 180 >/dev/null
  gpull a "C:\\Bindery\\lab\\runs\\$RUN_ID.zip" "$out/client-a-run.zip" && python3 -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$out/client-a-run.zip" "$out/client-a" && rm "$out/client-a-run.zip"
  for side in a b; do
    gx $side "Get-Content C:\\Bindery\\appliances\\client-$side\\ra2yrcpp.log -EA SilentlyContinue" 120 > "$out/ra2yrcpp-client-$side.log" 2>&1 || true
    gx $side "Get-Content C:\\Bindery\\appliances\\client-$side\\syringe.log -EA SilentlyContinue" 120 > "$out/syringe-client-$side.log" 2>&1 || true
    gx $side "Get-Content C:\\Bindery\\appliances\\client-$side\\DDrawCompat-gamemd.log -EA SilentlyContinue" 120 > "$out/ddrawcompat-client-$side.log" 2>&1 || true
    # WER LocalDumps for gamemd.exe (README): list, never copy -- a full dump is large.
    gx $side "Get-ChildItem C:\\Bindery\\dumps -Filter *.dmp -EA SilentlyContinue | % { '{0} {1} {2}' -f \$_.LastWriteTime.ToString('s'), \$_.Length, \$_.FullName }" 60 > "$out/dumps-client-$side.txt" 2>&1 || true
  done
  # Tokens must not reach evidence: scrub anything that looks like one.
  # grep exits 1 when nothing matches -- the normal case -- which pipefail would turn into a failed run.
  { grep -rlE '"(accountToken|account_token|token)"' "$out" 2>/dev/null || true; } | xargs -r sed -i -E 's/("(accountToken|account_token|token)"\s*:\s*")[^"]+/\1<redacted>/g'
  log "evidence in $out"
  find "$out" -type f | sed "s|$out/|    |"
}

teardown() {
  agent_down
  cp_down; log "control plane stopped"
  "$TUNNEL" down >/dev/null || true; log "tunnel stopped"
  RUN_ID="" prepare_guests restore
}

case "$mode" in
  preflight) preflight; exit $? ;;
  teardown) teardown; exit 0 ;;
esac
[ "$stage" = 1 ] || [ "$stage" = 2 ] || { echo "--stage 1|2 is required" >&2; exit 2; }
[ -n "$A_IP" ] && [ -n "$B_IP" ] || { echo "the guest agent reported no address (client-a '$A_IP', client-b '$B_IP'); run --preflight" >&2; exit 1; }
MATCH_RC=0
RUN_ID="$(date +%Y%m%d-%H%M%S)-s$stage"; RUN="$LAB/runs/$RUN_ID"; mkdir -p "$RUN"
exec > >(tee -a "$RUN/lab-run.log") 2>&1
log "run $RUN_ID"
push_payload
prepare_guests "${PREPARE_STAGE:-$stage}"  # PREPARE_STAGE=2 puts the fork DLL under a stage-1 match (bisection)
agent_up
[ "$mode" = prepare ] && { log "prepare-only: guests ready for stage $stage, client-b agent up; no match started"; exit 0; }
[ "$mode" = dry ] && log "dry run: preflight gate skipped" || preflight || { log "preflight failed; not starting the match (guests stay prepared; --teardown to undo)"; exit 1; }
trap 'log "tearing down"; teardown' EXIT
tunnel_up
cp_up
mint
render
"$HERE/relay-counters" baseline "$RUN"
run_match
collect
exit "$MATCH_RC"
