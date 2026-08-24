# ADR 0013 — Secrets never pass through IConfiguration

**Status**: accepted · 2026-08-16

## Context

Anything that can dump configuration — a diagnostic command, a log statement, a crash handler
— prints whatever configuration holds. An API key bills a card; the proxy token unlocks a
database of private conversations.

## Decision

Keys live in `ISecretStore` (DPAPI on Windows), addressed from configuration by **name** only
(`Model:ApiKeyName`). `EnvironmentOverrides` discards environment variables ending `_KEY` or
`_TOKEN`. Keys are never accepted pasted into chat or on a command line. The proxy requires
its own bearer — a different secret from the model key, because it gets typed into a third
party's settings — compared in constant time, and refuses to start without one.

## Consequences

- `airp secret set` is the single entry path; `DescribeAsync` answers "which key is winning"
  without any code path that prints a value.
- A sandbox home (`AIRP_HOME=.airp-dev`) has no key and therefore cannot spend money — which
  is why the sandbox launch profile is listed first.
