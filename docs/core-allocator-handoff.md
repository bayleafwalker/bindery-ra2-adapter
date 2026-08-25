# External-runtime allocator handoff

The adapter’s live profile requires the coordinator to issue a private
`cncnet-private` placement. The accepted core branch originally exposed the
generic allocator seam but its production entry point still constructed the
reference service without an allocator. The isolated handoff commit below
wires deployment configuration into that entry point:

```text
repository: bayleafwalker/bindery-core
branch: feat/external-runtime-w0-w1
commit: beef3f5
```

The commit is intentionally provider-specific for this first lab: it accepts
only `cncnet-private`, requires a `host:port` endpoint, defaults the region to
`eu-north`, creates a unique allocation ID per session, and keeps the
in-memory service at one replica. It does not accept a client-supplied relay
endpoint.

Required deployment environment:

```text
BINDERY_EXTERNAL_RUNTIME_RELAY_PROVIDER_ID=cncnet-private
BINDERY_EXTERNAL_RUNTIME_RELAY_ENDPOINT=<private-tunnel-address>:50001
BINDERY_EXTERNAL_RUNTIME_RELAY_REGION=eu-north
BINDERY_EXTERNAL_RUNTIME_RELAY_POLICY_VERSION=cncnet-private/v1
```

The deployment must use one replica until the service state is moved to a
shared durable store. The endpoint is non-secret placement configuration; the
identity, join, and enrollment credentials remain request-scoped secrets.

This commit exists in the isolated accepted-core handoff working copy used for
the 2026-08-24 gate. It must be reviewed and applied to the canonical
`bindery-core` branch before production deployment; this adapter repository
does not vendor or silently mutate that separate repository.
