# ADR 0012 — The library is text files, referenced by name, resolved by one rule

**Status**: accepted · 2026-08-15

## Context

Characters and personas are rewritten as they are played in. A copy taken at conversation
creation freezes each story at whatever the description said that day.

## Decision

Four shelves of plain text files under the data directory — `characters/`, `personas/`,
`snippets/`, `openings/` — managed in the TUI and by CLI verbs; `edit` opens the system
editor. A conversation stores the **name**, not a copy. One resolution rule for characters
and personas alike:

```
the conversation's own text  →  file by name  →  default file
```

An opening's filename matching a character's name *is* the association. Descriptions never
live in configuration; `Airp:DefaultPersona` names a file.

## Consequences

- Editing a file reaches every conversation using it — the point of files.
- There is deliberately no "the only file in the folder" branch: it changes meaning the day a
  second file appears. And a name defined in both file and configuration once silently
  preferred the wrong one.
- Character sheets and personas live outside the repository on purpose: they are personal
  writing, not sample data.
