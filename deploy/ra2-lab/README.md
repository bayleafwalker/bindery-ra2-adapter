# RA2 lab host

The lab is the golden Windows 11 appliance and its two clones,
`bindery-ra2-client-a` and `bindery-ra2-client-b`, running under
`qemu:///session` on the workstation. The host side is gitops-nixos
`profiles.ra2Lab`:

- libvirtd with swtpm on the PATH, because the session daemon needs it;
- the default NAT network `virbr0` on `192.168.122.1`, started and autostarted;
- qemu-bridge-helper allowed on `virbr0`.

## Drive the guests without a console

The golden image has the QEMU guest agent installed (`C:\Program Files\Qemu-ga`),
and the clones inherit it. Each domain has an `org.qemu.guest_agent.0` channel,
so the host runs commands inside a guest with no network, SSH or firewall rule
involved:

```bash
deploy/ra2-lab/ra2-vm-exec bindery-ra2-client-b 'hostname; Get-Volume | Select DriveLetter,FileSystemLabel'
```

Commands run as `NT AUTHORITY\SYSTEM`. The helper waits for the command, prints
its stdout and stderr, and exits with its exit code (124 on timeout; the default
timeout is 120 s). The guests classify `virbr0` as a **Public** network, so
Windows Firewall drops unsolicited inbound traffic, ping included. That is
expected: use the agent, not the network, to reach a guest.

## Define the domains on a new host

```bash
deploy/ra2-lab/import-domains.sh <old>/.config/libvirt/qemu
```

The script is idempotent:

- It copies the firmware variables (`nvram/`) and TPM state (`swtpm/<uuid>/`)
  and never overwrites existing copies. The guests are Secure Boot and need both.
- It rewrites the other distribution's QEMU and edk2 paths to the NixOS ones.
- It adds the guest-agent channel and defines the domains.

The disk images stay in `/projects/bindery-vm/images`, which is scratch storage.
Take a `qemu-img snapshot -c <tag>` of an image before changing it offline.

## Shutting down

The logged-in desktop ignores ACPI power requests. Stop a guest through the agent:

```bash
virsh -c qemu:///session qemu-agent-command bindery-ra2-client-a '{"execute":"guest-shutdown"}'
```

Use `virsh destroy` only when the agent is not answering.

## Unattended matches: `lab-run.sh`

One host command prepares both guests, starts the host services, plays one
match, collects evidence and restores the guests:

```bash
export RA2_LAB_HOME=/projects/bindery-vm/lab-kit-2026-09-27/lab   # bin/, payload/, secrets/, runs/
export RA2_LAB_TUNNEL=<path>/run-private-tunnel.sh                # `up` / `down` the pinned tunnel container
deploy/ra2-lab/lab-run.sh --preflight     # checks only; starts nothing
deploy/ra2-lab/lab-run.sh --stage 1       # LiveAcceptance: player-a vs 2 allied AI, player-b spectating
deploy/ra2-lab/lab-run.sh --stage 2       # Channel: player-a + agent seat player-b vs 2 allied AI, fork ra2yrcpp
deploy/ra2-lab/lab-run.sh --teardown      # stop everything, restore stock ra2yrcpp
```

`--prepare-only --stage N` stops after preparing the guests and starting the
client-b agent. `--dry-run --stage N` skips the preflight gate and caps the
wait. A run exits with the match's status: 0 only if the harness exited 0,
124 if the match was stopped at the timeout, and 1 otherwise. The agent token
reaches curl as a header file and guest files are written through virsh on
stdin, so neither shows up in a process's command line.

`RA2_LAB_HOME` stays out of the repository: it holds game content
(`payload/spawnmap-brutal.ini`) and tokens. `build-payload.sh [--fork-dir <dir>]`
rebuilds `payload/` from this checkout. The guest addresses come from the guest
agent (`virsh domifaddr --source agent`). Games start in the desktop session
through the on-demand task `\Bindery\BinderyLabRun`; no other task, service or
autologon is used.

A stage-1 match ends by itself when player-a is defeated. The two AI houses are
on one team (`SpawnAiParticipant.Team`, rendered as `[Multi{n}_Alliances]`):
unallied, they fought each other and never went near the idle human, and the
match ran to the timeout.

### Reading a run

Evidence lands in `$RA2_LAB_HOME/runs/<id>/evidence/`. Before calling a match
played, check the telemetry, not only the harness:

- `telemetry.txt` must show thousands of records per client. The capture is a
  gzip stream of varint-length-delimited `ra2yrproto` `GameState` messages; a
  partial trailing record is normal when the game is stopped.
- `control_plane_lifecycle_complete=True` alone does not prove the game
  played: the control plane winds down cleanly around a game that quit on frame 0.
- Syringe reports exit code 3 for a normal QuickExit ending too, so the exit
  code does not distinguish a played match from a failed one.
- A game that dies at startup shows an NTSTATUS exit code in
  `syringe-client-*.log` (e.g. `C00000FD`, a stack overflow); the harness then
  records that client as failed. The startup stack overflows seen so far were
  inside the game tree's `DDRAW.dll` (DDrawCompat 0.5.4, offset 0x1C104).
  `ddrawcompat-client-*.log` keeps DDrawCompat's own log for each run. Windows
  Error Reporting is set to write `gamemd.exe` dumps to `C:\Bindery\dumps` on
  both guests (key `HKLM\SOFTWARE\Microsoft\Windows\Windows Error
  Reporting\LocalDumps\gamemd.exe`, delete it to revert); it may not fire
  while Syringe is attached as the debugger.
- A module that faults at a randomised address can be named on the same boot:
  DLL bases stay fixed per boot, so the 32-bit module list of a running
  `gamemd.exe` (`C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe`,
  `(Get-Process gamemd).Modules`) shows which module holds the address.

`PREPARE_STAGE=2 lab-run.sh --stage 1` plays the stage-1 shape on the fork DLL,
which separates the DLL from the agent seat when stage 2 fails.

### Building the fork ra2yrcpp DLL

Build `bayleafwalker/ra2yrcpp` with upstream's docker MinGW toolchain (Ubuntu
24.04, `g++-mingw-w64-i686-posix`). It links libstdc++ statically, so the game
tree needs only `libra2yrcpp.dll` and `zlib1.dll`, like the stock appliance. A
nix GCC 15 build that uses the mcfgthread runtime (`libmcfgthread-2.dll`) made
the game quit on frame 0, with nothing recorded.

```bash
git clone --recurse-submodules https://github.com/bayleafwalker/ra2yrcpp && cd ra2yrcpp
docker compose build builder
docker run --rm -u "$(id -u):$(id -g)" -v "$PWD":/home/user/project -w /home/user/project \
  -e HOME=/tmp shmocz/ra2yrcpp:latest ./scripts/tools.sh build-cpp
deploy/ra2-lab/build-payload.sh --fork-dir <ra2yrcpp>/cbuild/mingw-w64-i686-Release/pkg/bin   # from the adapter checkout
```

The image is built locally and not pushed. In a git worktree, also mount the
main repository's `.git` read-only at the same path, because the build calls
`git rev-parse`.
