# ADR 0019 — A story's model is checked, fitted, and never the reason a turn is lost

**Status**: accepted · 2026-09-30

## Context

Every story was played on one configured model. Some are better on another — one more willing
to write the scene, or with a different voice — and the prompt is rebuilt from the store on every
send, so nothing already written belongs to the model that wrote it. The record had a `Model`
column the reply path already honoured; nothing set it.

Three ways that goes wrong were known before any of it was built. A mistyped identifier fails
every turn it is used for. A model with a smaller window than the budget refuses every prompt
that fills the budget. And a model that is there today can have no host tomorrow.

## Decision

- **Checked before it is saved.** A model is saved only if the provider's list has it, with the
  window the list gives. One not listed, or not checkable because the list cannot be read, is not
  saved; the reader is told, and the story stays on what it had.
- **The budget fits the window.** A story on a model whose window, less the reply's ceiling, is
  under the configured budget gets that as its budget — one copy of the settings, handed to the
  summariser, the retriever and the builder alike, since those three disagreeing is what once lost
  twenty-four turns of a real story.
- **A missing model does not lose the turn.** When the provider answers that the story's model
  does not exist or has no host, the default writes that turn, the reply records `FellBackFrom`,
  and the screens say so. The story keeps its model and tries it again next turn. Any other
  refusal — key, credit, a request the default would refuse too — is reported, not retried.
- **Memory stays on the default.** Summaries and facts are written by the configured model (or
  `backgroundModel`) whatever the story is played on, so every story's memory is written alike.
- **Offered from a list.** `model.choices`, unset by default to ten roleplay finetunes chosen to be
  more willing than the default. Labelled with the provider's list prices, to compare by only: what
  a call cost is still read from its response (ADR 0009).

## Consequences

- One GET of a public list per ten minutes, and none on a send.
- A story on a 32k model compresses far sooner than at a 90k budget, and its audit says the
  budget was shrunk.
- A reply written by the default carries a different voice, and a warning says so for as long as
  it is the newest turn.
- Whether a model is as uncensored as its reputation is not something the list can say, nor
  whether its host filters. The audit's *served by* is where that shows.
