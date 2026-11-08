# Changelog

Notable user-facing changes will be documented here.

## Unreleased

- Allow remote MCP upstreams only after explicit per-server approval of an HTTPS origin; keep loopback endpoints directly available.
- Require the approved origin to match the configured host and port, reject remote plain HTTP, and prevent imported JSON from granting trust.
- Keep internal Bridge listeners and the internal router restricted to loopback.
