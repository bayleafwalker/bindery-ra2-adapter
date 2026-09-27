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
