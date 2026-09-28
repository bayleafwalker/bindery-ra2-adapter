#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Define the RA2 lab domains (golden, client-a, client-b) under qemu:///session
# on a NixOS host (gitops-nixos profiles.ra2Lab), from domain XMLs written on
# another distribution, and give each a QEMU guest-agent channel so the host can
# drive the guests with ra2-vm-exec instead of a console.
#
#   import-domains.sh <old-libvirt-qemu-dir>
#
# <old-libvirt-qemu-dir> holds <name>.xml, nvram/<name>_VARS.fd and swtpm/<uuid>/
# (e.g. the old home's .config/libvirt/qemu). Firmware variables and TPM state are
# copied, never moved: the guests are Secure Boot Windows 11 and need both. A domain
# that is already defined keeps its state; only its definition is refreshed.
set -euo pipefail

source_dir=${1:?usage: import-domains.sh <old-libvirt-qemu-dir>}
uri=qemu:///session
target=${XDG_CONFIG_HOME:-$HOME/.config}/libvirt/qemu
names=(bindery-ra2-golden bindery-ra2-client-a bindery-ra2-client-b)

command -v swtpm >/dev/null || {
  echo "swtpm is not on PATH; the session daemon needs it for the guests' TPM (profiles.ra2Lab provides it)" >&2
  exit 1
}

mkdir -p "$target/nvram" "$target/swtpm"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

for name in "${names[@]}"; do
  xml=$source_dir/$name.xml
  [[ -f $xml ]] || { echo "skip $name: no $xml" >&2; continue; }
  uuid=$(sed -n 's#.*<uuid>\(.*\)</uuid>.*#\1#p' "$xml")

  cp -n "$source_dir/nvram/${name}_VARS.fd" "$target/nvram/"
  [[ -d $source_dir/swtpm/$uuid ]] && cp -rn "$source_dir/swtpm/$uuid" "$target/swtpm/"
  chmod -R u+rw "$target/nvram" "$target/swtpm"

  # Arch paths -> the NixOS libvirt paths for the same QEMU and 4 MB edk2 layout.
  sed -e 's#/usr/bin/qemu-system-x86_64#/run/libvirt/nix-emulators/qemu-system-x86_64#' \
      -e 's#/usr/share/edk2/x64/OVMF_CODE.secboot.4m.fd#/run/libvirt/nix-ovmf/edk2-x86_64-secure-code.fd#' \
      -e 's#/usr/share/edk2/x64/OVMF_VARS.4m.fd#/run/libvirt/nix-ovmf/edk2-i386-vars.fd#' \
      -e "s#[^'<>]*/libvirt/qemu/nvram/#$target/nvram/#" \
      "$xml" > "$work/$name.xml"

  # The guest agent (installed in the golden image) needs a virtio-serial channel.
  if ! grep -q org.qemu.guest_agent.0 "$work/$name.xml"; then
    python3 - "$work/$name.xml" <<'EOF'
import sys
path = sys.argv[1]
text = open(path).read()
channel = """    <channel type='unix'>
      <target type='virtio' name='org.qemu.guest_agent.0'/>
    </channel>
"""
anchor = text.index("  </devices>")
open(path, "w").write(text[:anchor] + channel + text[anchor:])
EOF
  fi

  if grep -q /usr/ "$work/$name.xml"; then
    echo "$name: unconverted host paths remain:" >&2
    grep -n /usr/ "$work/$name.xml" >&2
    exit 1
  fi
  virsh -c "$uri" define "$work/$name.xml"
done

virsh -c "$uri" list --all
