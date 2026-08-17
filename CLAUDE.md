# Airp — a terminal roleplay client with a memory of its own

Permanent context for this project. Read it in full before any task.

---

## What we want

**An NSFW roleplay chat in the terminal, that does not cost a monthly subscription, with a
memory that does not forget.**

Every decision is justified against that sentence. If something does not move towards it, it
does not belong.

Where it came from: $20/month was being paid to ourdream.ai. JanitorAI is free, but its
memory and its context handling are bad. Neither one leaves the history on the owner's
machine.

The thesis, in one line:

> You do not need an infinite context window if you have a good retrieval layer.

**This repository is one application.** The ourdream browser client this grew out of lives in
its own private repository, frozen where it worked. Nothing here drives a browser, holds a
site session, or knows any site's markup. If a task seems to need any of that, it belongs in
the other repository — or nowhere.

---

## Hard limit, not negotiable

**No automated access to JanitorAI is implemented.** No authenticating against
`janitorai.com`, no private endpoints, no scraping, no browser automation. Their Terms of Use
prohibit bots and scripts.

The only interaction with Janitor is **passive**: Janitor calls us because the user
configured a Proxy URL. Never the other way round. If a task appears to require crossing this
line, stop and say so. Do not look for a way around it.

---

## How it is laid out

```
src/
  Airp.Domain/          domain, exceptions, AppPaths
  Airp.Application/     abstractions, context, options, services
  Airp.Infrastructure/  the local store, model clients, secrets
  Airp.Terminal/        the TUI — Spectre.Console, views, shell
  Airp.Proxy/           OpenAI-compatible endpoint, for playing from Janitor
tests/Airp.Tests/       508 tests
tools/ollama/           the Rocinante Modelfile the community mirror did not ship
docs/                   MANUAL.md — the only document that ships
```

**The provider seam survives the split on purpose.** The terminal talks only to
`IChatProvider` and `IConversationProvider`; `LocalConversationProvider` answers both, and
`Airp:Provider` selects by name with `local` as the only registered value. A second backend is
one registration away, and the terminal would never know.

**Careful with `IChatProvider`:** it means "list the conversations". The model client is
`ILanguageModelClient`. Do not confuse them.

**Layering rule:** `Airp.Terminal` and `Airp.Proxy` only translate formats. `Airp.Application`
knows nothing about HTTP.

**Data lives in `%LOCALAPPDATA%\Airp`** — database, characters, personas, logs, config.
`AIRP_HOME` overrides it, which is how the QA harness isolates itself. The ourdream-era client
keeps its own separate folder; the two never touch.

**The working papers are private.** The QA plan and its scripts, the roadmap, the probe
protocol, the handoff prompt and the phase-0 research live in `docs-private/` under the data
directory, not in the repository. Only `docs/MANUAL.md` is public. They stayed in the git
history when they left.

---

## The library

The library is **text files**, on four shelves under the data directory:

```
characters/   the definition of a character or a world
personas/     who you are in the scene
snippets/     authored prose the composer expands via :name + Tab
openings/     first messages, named after the character they belong to
```

The opening's filename matching a character's name IS the association — the new-chat flow
pre-fills it from that match alone. Snippets share the emoji shortcode rail in the composer.
All four are managed in the TUI (`M` in the chat list) or by CLI verbs; `edit` always opens
the system editor — composers make messages, editors edit files.

A conversation stores **the name**, not a copy. Editing the file reaches every conversation
using it — which is the reason they are files at all.

**One resolution rule, identical for both:**

```
the conversation's own text  →  file by name  →  default file
```

The conversation's own text always wins: it was written for that story and cannot have been
written for another. There is no fourth branch: an earlier version used "the only file in the
folder" when there was exactly one, and that changes meaning the day a second one appears.

`Airp:DefaultPersona` names a file. **Descriptions do not live in configuration** — an earlier
version accepted them there too, and a name defined in both places silently preferred the
wrong one.

---

## The prompt, in layers

**The order is a contract, not a preference.** They run from least to most volatile, and a
provider's prefix cache keeps everything up to the first thing that changed since the previous
turn.

| Layer | Changes |
|---|---|
| `character` | never |
| `persona` | almost never |
| `directives` | the dials, inner thoughts |
| `world` | when the story establishes or contradicts something |
| `summaries` | when a stretch is compressed |
| `history` | append-only |
| `memories` | **every turn** |
| `trackers` | **every turn** |
| `instruction` | per call |

Putting retrieval in the middle — the instinctive place — would break the cache on every
turn. With a local model that is the difference between seconds and minutes; with a caching
API, between $0.0028/M and $0.14/M.

**When the budget is tight, what gives is the transcript**, oldest first. Everything else is
small, or was chosen for this turn.

The default budget is 32,000 tokens, far below the million the model accepts. A large window
is not a reason to fill it: attention thins out, and every token is paid for on every turn.

**Token counting uses the real o200k vocabulary**, embedded, not a characters-per-token
constant. Spanish runs at ~3.6 chars/token and English at ~4.7; no constant works for someone
who writes in both. It is verified against a number the provider reports.

---

## The memory, which is three different things

Each answers a question the other two do not:

| | Question | How |
|---|---|---|
| **Summaries** | what happened? | the model compresses the stretch that no longer fits |
| **Retrieval** | what was said? | embeddings + cosine over already-compressed turns |
| **Facts** | what is true now? | extracted from the same stretch, with `ValidFrom`/`ValidTo` |

**All three fire on their own, and only when needed.** A conversation that fits in the budget
does not spend one extra call.

Summaries and facts are produced at the same point: the moment some turns stop being seen in
their own words is the last chance to get anything cheap out of them.

If compressing fails, **go over budget** rather than discard. Going over costs cents; a
character that has forgotten is irreversible — and discarding is exactly what the services
this project replaces do.

Retrieval only embeds turns that have **already been compressed**: the recent ones go in
whole anyway.

**Facts can be written by hand** with `airp fact add`, and those are pinned: the extractor
cannot retire them, the reader can. Without that exit, a badly extracted fact is injected, the
character acts on it, the transcript confirms it, and the error reinforces itself.

---

## The dials

Three, per conversation, with the scale configurable in `Airp:Scales`:

| Dial | Where it goes |
|---|---|
| Creativity | the sampler's **temperature**: 0.6 to 1.4 |
| ResponseLength | the **token ceiling**: 200 to 2600, and into the prompt |
| Lust | **into the prompt**, with the scale's own wording |

Creativity deliberately does not reach the prompt: asking a model to be "more varied" is a far
weaker lever than temperature.

The text the reader sees on screen and the text the model receives **are the same**. Sending
different wording would make the dial mean two things.

When summarising, temperature is pinned at 0.3 regardless of Creativity: a creative
summariser invents details the character will then believe forever.

---

## Optional, per conversation

**Inner thoughts** (`airp thoughts on`) — each character adds one line of what they did not
say. It is the only thing a scene with a model cannot give you any other way. Never for the
user, and if it only repeats what was said out loud, it is omitted.

**Trackers** (`airp tracker add`) — meters with a free-form name, a stored value, and fields
for what they measure, what their numbers mean, and what rule constrains them. The value is
injected, the model draws it moved, and it is read back: that is what makes it survive
compression.

Both off by default. **There is a legitimate reservation on record about trackers**: a meter
the model can see is a meter the model writes towards, and the scene starts arranging itself
to move the number.

---

## Data invariants

1. **`Messages` is append-only.** `AirpDbContext.SaveChanges` **refuses** to persist a message
   deletion or an edit to its text. The second matters more: an in-place edit loses what was
   said just as a deletion does, and looks innocent.
2. **Persist the user's turn BEFORE calling the model.**
3. **Idempotency by `RequestHash`**, anchored on **the last reply** — the state that message is
   answering. Anchoring it on the next free position gives a different hash on the retry and
   stores the line twice.
4. **Auditing is mandatory.** Every reply stores the per-layer breakdown, the estimated tokens
   and the reported ones. `airp audit` shows them. A budget nobody checks against reality
   stopped meaning anything, silently.
5. **`Facts` uses `ValidFrom`/`ValidTo`.** A fact stops being true without erasing history.
6. **Summaries, facts and embeddings are derived.** Dropping those tables entirely loses
   nothing: all of it can be produced again from `Messages`.

What the terminal sees as deleted, the database keeps with a tombstone. A reroll hides the
previous reply and leaves it in the audit — "why did it say that" is almost always asked about
a reply that was thrown away.

---

## Models

Learned at the cost of time. Do not learn it again.

- **Selection criterion #1 is uncensored — the most unfiltered model available.** Prose
  second, cost a distant third. A censored model breaks the Lust dial, makes the summariser
  refuse — and a summariser that refuses is a character that forgets. Beware that the same
  model can arrive differently filtered from different OpenRouter backends: on refusals,
  check the audit's `served by` before blaming the model.
- **At this volume, cost is noise.** A real 95-message session costs ~$0.12. The variable that
  matters is prose quality for NSFW RP.
- **DeepSeek dominates RP**: more than half of OpenRouter's roleplay traffic across its
  variants. V4 Flash: 1M context, $0.14/M in, $0.28/M out.
- **OpenRouter first, direct later.** One key to try many models. Only DeepSeek's own API and
  Azure cache prefixes; the cheap ones do not. Moving to direct is only worth it once you have
  already chosen a model.
- **The provider changes between requests** and is stored per message. If a scene comes out
  strange, that is the first thing to look at. Measured in practice: some of the providers
  OpenRouter fans this model across intermittently return a response with no content at all.
  The client keeps the message and says not to resend; the QA harness retries that case.
- **OpenRouter exposes `/embeddings`** — `openai/text-embedding-3-small`, 1536 dimensions, same
  key. At ~$0.02/M the whole corpus costs less than a cent.
- **DeepSeek's API is billed** and **does not expose `/v1/embeddings`**.
- **`gpt-oss-120b` is among the most censored there is.** The worst choice for NSFW RP.
- **Local Ollama is a privacy option, not a cost one**: 4.58 tok/s on this hardware.

Hardware: integrated Intel Arc, no discrete GPU, 63.5 GB RAM.

---

## Security

- API keys live in `ISecretStore` (DPAPI on Windows). **They do not pass through
  `IConfiguration`**: `EnvironmentOverrides` discards variables ending in `_KEY` and `_TOKEN`
  so that no configuration dump can print them.
- Never accept a key pasted into the chat or written on the command line.
- The proxy requires its own bearer, compared in constant time. **It does not start without a
  token.** And it is a different token from the model key: this one gets typed into a third
  party's settings.
- The database holds the entire history in the clear, and it is NSFW. Never expose a direct
  port: tunnel with TLS.
- `.gitignore` covers `*.db`, `captures/`, `secrets/`, `.env`, `exports/`.
- Character sheets and personas live outside the repository on purpose.

---

## How to work

- **Do not invent and do not assume.** If a fact is missing to make a decision, ask before
  implementing.
- **One change at a time**, and confirm before moving on.
- No speculative scaffolding.
- Tests where there is real logic. No tests of getters.
- **Named arguments on calls with many parameters.** Three separate bugs in a single day came
  from adding a layer in the middle of a positional list: the dials arriving as the character
  definition, and nobody finding out until a test failed.

**Language:** the repository is in English — code, comments, documents, tests and commit
messages. The owner writes in Spanish in conversation and plays in English.

---

## Current state

**Built, published, and one step from lived-in.** The repository is public at
`argamboad/custom-airp` (MIT). 508 tests. Zero warnings, enforced by
`TreatWarningsAsErrors`.

The TUI covers the full loop: `N` new chat (pickers + opening pre-fill), `M` the
four-shelf library manager, `S` dials + inner-thoughts toggle, snippets in the composer.

**Owner-specific state — the cast, the raw material, the machines — lives in
`CLAUDE.local.md`**, git-ignored beside this file. When it is present, read it too.

**Hard-won lessons encoded in tests — do not relearn:**

- The keymap resolves `n`/`N` to `SearchNext` in every mode. A view wanting N handles
  `SearchNext`, and **view tests must resolve strokes through the real `KeyMap`** — a
  hand-built stroke once shipped a dead key.
- The global tool refresh (`dotnet tool uninstall/install`) fails while any `airp` session
  runs — the exe is locked; it is not a build problem.
- OpenRouter fans the model across backends; some intermittently return empty content and
  some may filter differently. On refusals or empty replies, check the audit's `served by`
  column first.

**What has never run:** the memory against a real long conversation (compression, retrieval
and extraction have 500+ tests and zero lived turns — around message ~120 a turn runs slow;
`airp audit` and `airp fact` right after is the moment of truth), and the proxy against real
Janitor.
