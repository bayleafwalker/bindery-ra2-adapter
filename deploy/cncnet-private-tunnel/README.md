# Private CnCNet tunnel

This chart deploys the maintained CnCNet .NET tunnel image as a single,
non-public lab workload. It deliberately disables master announcement and
requires an immutable image digest.

The default `image.digest` is resolved from GHCR on 2026-08-24. Re-resolve and
review it when changing the upstream image; never deploy a mutable `latest`
tag.

```bash
helm upgrade --install bindery-private ./deploy/cncnet-private-tunnel \
  --namespace bindery-ra2 --create-namespace \
  --set image.digest='sha256:<reviewed-image-digest>' \
  --set service.type=LoadBalancer
```

Use the externally reachable service address and tunnel V3 port as
`BINDERY_EXTERNAL_RUNTIME_RELAY_ENDPOINT` in the external-runtime deployment.
The CnCNet server must remain private to the two Windows clients and the
control-plane environment. Packet counters for the allocated endpoint are
independent qualification evidence.
