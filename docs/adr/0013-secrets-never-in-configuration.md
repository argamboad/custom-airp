# ADR 0013 — Secrets never pass through IConfiguration

**Status**: accepted · 2026-08-16

## Context

Anything that can dump configuration — a diagnostic command, a log statement, a crash handler
— prints whatever configuration holds. An API key bills a card; the proxy token unlocks a
database of private conversations.

## Decision

Keys live in `ISecretStore`, addressed from configuration by **name** only
(`Model:ApiKeyName`). On Windows the store encrypts each secret with DPAPI at user scope —
useless to another account, useless copied off the machine. On Linux and macOS, where DPAPI
does not exist, `airp secret set` refuses and the store falls back to an environment variable
of the same name, read directly rather than through configuration; a stored secret wins where
both exist. `EnvironmentOverrides` discards environment variables ending `_KEY` or `_TOKEN`,
so the fallback never leaks into a configuration dump. Keys are never accepted pasted into
chat or on a command line. The proxy requires
its own bearer — a different secret from the model key, because it gets typed into a third
party's settings — compared in constant time, and refuses to start without one.

## Consequences

- `airp secret set` is the single entry path; `DescribeAsync` answers "which key is winning"
  without any code path that prints a value.
- A sandbox home (`AIRP_HOME=.airp-dev`) has no key and therefore cannot spend money — which
  is why the sandbox launch profile is listed first.
