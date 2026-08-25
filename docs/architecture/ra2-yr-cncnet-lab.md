# Bindery Yuri's Revenge / CnCNet lab

The live acceptance target is Yuri's Revenge in CnCNet RA2 Mode, launched by
the maintained `yrpp-spawner` through Syringe and connected through a private,
self-hosted CnCNet-compatible tunnel.

```text
Bindery control plane
  identity / session / placement / lifecycle / evidence
             |
       cncnet-private placement
             |
     private CnCNet-compatible tunnel
        /                         \
golden VM clone A             golden VM clone B
  YR + CnCNet package           YR + CnCNet package
  Syringe + spawner             Syringe + spawner
  instrumentation               instrumentation
        \                         /
         ra2yrcpp-derived protobuf/TCP
                       |
                Bindery telemetry agent
```

## Ownership boundaries

Bindery owns identity, session allocation, client enrollment, match metadata,
lifecycle reports, telemetry ingest, replay/observation evidence, and the
qualification packet. The CnCNet-derived tunnel owns RA2/YR packet transport
and NAT/tunnel behavior. The Westwood executable owns simulation, lockstep,
and game rules. The native `bindery-native` relay is retained for contract and
loopback tests; it is not the live profile.

`CncNetPrivateMatchDriver` therefore validates the coordinator-issued
`cncnet-private` placement and endpoint but does not implement or emulate the
CnCNet tunnel protocol. The tunnel process and its packet counters are an
external lab workload. This keeps the boundary honest until the control-plane
placement contract and tunnel deployment are frozen together.

## Runtime profile

| Field | Live value |
| --- | --- |
| Game family | `yuris-revenge` |
| Ruleset | `cncnet-ra2-mode` |
| Launch | `Syringe.exe gamemd.exe -SPAWN -CD -LOG` |
| Transport | `cncnet-private` |
| Telemetry | `ra2yrcpp-protobuf-tcp`, local port `14521` by default |
| Libvirt host gateway | `192.168.122.1` (`virbr0`; private lab path) |
| Public CnCNet | prohibited for instrumented clients |

The native telemetry fork owns protobuf framing and generated messages. This
repository consumes normalized `RawObservation` values and records source
ranges; it does not vendor the native DLL or invent a competing decoder.

The maintained CnCNet .NET tunnel image is an external workload. Pin an image
digest in the deployment, expose only the required TCP/UDP tunnel ports, and
run it with `--no-master-announce` so instrumented clients never enter the
public tunnel directory. In the current libvirt topology bind the host-side
listener to `192.168.122.1` and issue that endpoint as `cncnet-private`; the
instrumentation allowlist must admit the same private gateway. The coordinator
must issue the resulting private endpoint as `cncnet-private`.

The later Chrono Divide adapter is a semantic challenger, not a dependency of
the first RA2 acceptance.

## Qualification consequence

The live evidence packet must show:

- one shared golden appliance ID and identical executable hashes on both
  clients;
- distinct VM/runtime identities, Bindery accounts, and client instance IDs;
- a coordinator-issued `cncnet-private` allocation;
- actual tunnel traffic counters tied to that allocation;
- lifecycle completion and final departed enrollment state;
- observed ra2yrcpp events, Kctl `knowledge.candidate.intake` authority,
  traced oracle reads, and human acceptance.

Until the private tunnel placement is served by the control plane and those
external evidence inputs exist, global qualification remains false.
