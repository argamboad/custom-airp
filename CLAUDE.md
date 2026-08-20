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
tests/Airp.Tests/       679 tests
tools/ollama/           the Rocinante Modelfile the community mirror did not ship
docs/                   MANUAL.md — the only document that ships
src/Airp.Infrastructure/Samples/   the worked example, embedded; `airp library --samples`
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

**Compression goes in batches — at least 10 messages, at most 40, never touching the 6 most
recent.** Once the transcript sits at the ceiling, every send overflows by exactly the exchange
just added, so compressing only the overflow runs every turn on one or two messages. Measured on
the real BJU story: 37× over 62 messages, 1.06× over two, and one two-message stretch whose
summary came out **longer** than the turns it replaced. The extractor was damaged by the same
thing — four runs in a row returned empty arrays, correctly, because nothing durable is
established in two messages.

**The cap matters as much as the floor, and cost more to learn.** The first version had only a
floor, so a rebuild handed the whole 99-message backlog to one call against a fixed 700-token
output ceiling — and the answer was `##`. Two characters, non-empty, stored, standing in for the
first hundred turns of a real story while those turns left the prompt. **A summary is now
refused unless it is long enough to be an account of what it replaces** (`ConversationSummariser.
Credible`, deliberately far looser than any observed ratio), which reaches the branch that
already existed for a summary that could not be written at all: send the turns whole and go over
budget. An empty reply was checked for; a useless one was not, and useless is what a bad host
returns.

**Two shapes of bad summary, both refused, and one guard too many.** Empty was checked from the
start; too short to be an account of its stretch is `Credible`, a compression ratio above 60×
against 3–19× for every one that worked. A summary cut off mid-word — the third shape, and real
— fails that same ratio, so **the extra "was it truncated" guard written for it was removed the
same hour**: keyed on a fraction of the output ceiling, it refused a 582-token account of ten
messages for being eighteen tokens under an arbitrary line. A fraction of the ceiling says
nothing about whether an answer covers what it replaced. And refusing a clipped-but-substantial
summary is the worse failure anyway — against a host that always clips it means never
compressing, and a transcript permanently over budget beats no summary only in theory.

**The ceiling itself was measured too low at 700 and is now 1200** — three of four summaries of
a real 40-message batch ended mid-word against it.

**A 200 with no message content now says why**, and the first thing it said solved a bug. It is the most common failure the background
readers hit, and the message could not tell a host that generated nothing from one that refused:
`finish_reason` separates `content_filter` from `length` from `stop`, and a `reasoning` field
with null content is a third thing again. Five extractions in a row died on this during one
rebuild, and none of them said which.

The answer was `finish_reason: length, served by GMICloud, reasoning only` — **the model spent
its whole output budget thinking and had none left to answer with.** Summarising is generative
and prose starts on the first token; extraction is analytical, and a reasoning model deliberates
before it writes any JSON. So extraction has its own `ModelTask.Facts` now, with a ceiling far
above what the answer needs, because the answer is not what fills it. Reasoning tokens are
billed as output — but an extraction that never lands costs the same and buys nothing.
**OpenRouter's `reasoning` request field (`effort`, `max_tokens`, `exclude`, `enabled`) is the
sharper lever and is not used yet**; it is provider-specific like `provider`, and worth reaching
for only if the ceiling turns out not to be enough.

**The reader is named by their persona, never "User".** Both background readers used to label
the reader's turns `User`, and the extractor — told that a subject is a character's name —
filed everything about them under `User`, directly beneath a summary calling the same person by
name. `Transcript` is the one place that renders a stretch for a model to read; there is no
second copy to drift.

**`airp rebuild <chat> --yes` spends invariant 6 deliberately.** It deletes the summaries and
the extracted facts and produces them again by replaying `ComposeAsync` until nothing more
compresses — the ordinary send path, not a second implementation of the rules, so a rebuild
cannot produce a memory the application never would. **Pinned facts are kept**: they are the one
thing here derived from nothing. The ledger is added to, never reset. Without `--yes` it prints
what it would replace and touches nothing.

**Background calls are retried once**, and only for failures a second attempt could answer
(no status, 200-with-no-content, 408, 429, 5xx). A rejected key fails identically twice and
costs twice. This exists because a failed reply is visible and a failed summary is a log line:
the real story lost the extraction over its first 62 messages to a single empty response, and
nothing will ever look at those turns again.

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
7. **`Spend` is a ledger, and is the one table that is *not* derived.** One row per billed
   call — replies, asides, compression, extraction — written whatever became of the output. A
   reply rerolled a second later cost exactly what a kept one did. Token counts could be
   re-estimated; what a router actually charged, after its pricing, its choice of host and
   whatever cache discount applied that day, exists nowhere else once the response is gone.
   Whether a reply was discarded is read from its tombstone at report time, never stored, so a
   turn rerolled tomorrow counts as discarded tomorrow.
8. **`Asides` never enters a prompt.** A question asked out of character is not a turn.
   Stored as one, retrieval would embed it, the summariser would compress it as something that
   happened and the extractor would pull facts from it — and append-only makes all of that
   permanent.

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
  already chosen a model. **Embeddings can be split off** with `EmbeddingBaseUrl` and
  `EmbeddingApiKeyName`, which is what makes going direct survivable: DeepSeek exposes no
  `/embeddings`, and without the split, pointing the whole client at it takes retrieval away
  silently. Both fall back to the chat settings when unset.
- **OpenRouter is the only API this has ever run against.** The request is plain OpenAI and
  should reach anything speaking it, but three response fields are OpenRouter's own —
  `provider`, `usage.cost`, and `prompt_tokens_details.cached_tokens` (DeepSeek names its
  cache counters `prompt_cache_hit_tokens`/`prompt_cache_miss_tokens` instead). Everything
  built on them degrades to silence rather than to a wrong number, and `Cost` is nullable
  throughout for exactly that reason. The repository is public; this is the obvious place for
  someone else's provider to be added.
- **The provider changes between requests** and is stored per message. If a scene comes out
  strange, that is the first thing to look at — and it decides the bill as much as the prose:
  whether the host caches prefixes at all varies between them, measured at 61% against 0% for
  the same conversation on the same day. Measured in practice: some of the providers
  OpenRouter fans this model across intermittently return a response with no content at all.
  The client keeps the message and says not to resend; the QA harness retries that case.
- **OpenRouter exposes `/embeddings`** — `openai/text-embedding-3-small`, 1536 dimensions, same
  key. At ~$0.02/M the whole corpus costs less than a cent. Not counted in the spend ledger,
  and the report says so rather than implying completeness.
- **The real cost of a call comes back inline, on every response**, in `usage.cost`, with
  `prompt_tokens_details.cached_tokens` beside it. Nothing has to be asked for: the
  `usage: {include: true}` request flag is deprecated and ignored. `GET /api/v1/generation?id=`
  gives the same figures authoritatively but costs a second round trip and documents no
  consistency guarantee, so the generation id is stored and the endpoint left for reconciling.
  **Never compute cost from a price list** — prices change, hosts differ, caching discounts.
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
`argamboad/custom-airp` (MIT). 679 tests. Zero warnings, enforced by
`TreatWarningsAsErrors`.

**The new-chat picker previews the card from `=== THE WORLD ===` and prints what it costs.**
Not a `PREVIEW` section in the card — that would be dead weight in the character layer on every
turn forever — and not a fifth shelf, which would be a second copy of the same paragraph going
stale. The skeleton already asks for that section to be *"a place a reader can arrive at, not a
synopsis"*, so it is preview copy already, it is already sent, and it cannot drift.
`TextLibrary.Preview` stops at the next `=== ` header and falls back to the top of the file for
cards that do not follow the skeleton. The token count beside the name turns warning-coloured
above 20k: it is the number that decides whether a story compresses at turn twenty or turn two
hundred, and it used to be invisible until the reader was already playing.

The TUI covers the full loop: `N` new chat (pickers + opening pre-fill), `M` the
four-shelf library manager, `S` dials + inner-thoughts toggle, `B` branch from the cursor,
snippets in the composer.

**`B` branches a story at the cursor** into a named copy, so one scene can go two ways. The
copy carries the character, the persona, the dials, the visible transcript up to that turn and
the memory built from it — summaries wholly inside the branch, facts that were true at that
point (a later retirement is undone, since it was done by turns the copy does not have), and
the embeddings, which are the same vectors for the same text. **Three things are deliberately
left behind:** `Spend`, because a ledger of money actually charged must not be duplicated;
`RequestHash`, which is computed over the conversation's own id and so can never match in a
copy; and tombstoned replies, which belong to the original's audit. **Trackers are the one
thing that cannot be rewound** — a meter stores a value and the turn it last moved, not a
history — so a far-back branch carries the number forward and the reader has to correct it.
Verified against the real 237-message BJU story: 78 visible messages copied of 100 sequences,
zero hashes, zero ledger rows, dials and character intact.

**Twelve slash commands live in the composer**, because the alternative — typing
`(OOC: skip to the evening)` into a message — is permanent, billed, retrieved, summarised and
un-take-backable. `/do` steers a turn, `/ask` answers a question about the story and stores
the answer nowhere (`F` in the pane promotes it to a pinned fact), `/focus` hands the turn to
a named character; the rest read what is already on disk. **An unrecognised command is refused,
never sent**; `//` escapes a message that genuinely starts with a slash.

**Replies are drawn, not shown raw.** `*action*` renders italic and dimmed, `"speech"` renders
as plain text, and both lose their markers — display only, since the stored wording is what the
next prompt sends and what the prefix cache is keyed on. Single asterisks outnumber double ones
about ten to one in real replies; both are read.

**Owner-specific state — the cast, the raw material, the machines — lives in
`CLAUDE.local.md`**, git-ignored beside this file. When it is present, read it too.

**Hard-won lessons encoded in tests — do not relearn:**

- The keymap resolves `n`/`N` to `SearchNext` in every mode. A view wanting N handles
  `SearchNext`, and **view tests must resolve strokes through the real `KeyMap`** — a
  hand-built stroke once shipped a dead key.
- **A test that sets `CharacterDefinition` inline is not testing a real conversation.** Real
  ones name a file. Anything that reasons about the size of the prompt has to be tested against
  a resolved character, or it is being tested against four tokens. This one cost twenty-four
  turns of a real story.
- The global tool refresh (`dotnet tool uninstall/install`) fails while any `airp` session
  runs — the exe is locked; it is not a build problem.
- OpenRouter fans the model across backends; some intermittently return empty content and
  some may filter differently. On refusals or empty replies, check the audit's `served by`
  column first.

- **A directive sent bare gets echoed back as the reply.** Observed twice now, once as a
  carry-on that returned nothing and once as a regenerate note — `Use at least 30 words` —
  coming back verbatim as the character's turn. Every instruction-layer text must say what it
  is (a direction, out of character, not something anyone said) and what the reply must be
  (the scene itself, never a repetition or acknowledgement of the note). `LocalDirections`,
  `AskDirective` and `RegenerateDirective` all carry that frame; anything new must too.

**What has never run:** the proxy against real Janitor.

**The memory has now run against a real long conversation, and it failed.** A 202-message BJU
story with a 30k-token character had **zero summaries, zero facts and zero embeddings**, while
the audit read `history 28791 (24 dropped)`. Twenty-four turns left the prompt with nothing
written down about them — the exact forgetting this project exists to prevent, undetected
behind five hundred passing tests.

The cause: the summariser reserved room for the character by reading
`conversation.CharacterDefinition`, the conversation's *own inline text* — which is empty in
every conversation the application creates, because a conversation stores the **name** and the
text lives in a file. So it reserved zero for half the prompt, believed the transcript had
58,776 tokens when the builder gave it ~28,900, and never found anything to compress. The
builder dropped the turns instead.

**The lesson is bigger than the fix: every test set the character inline.** `SummaryTests` uses
`CharacterDefinition = "You are Elena."`, four tokens, so the wrong reservation could not show.
Five hundred tests of the memory, and not one of them built a conversation the way the
application does. `CharacterInAFileTests` now does, with a real file and a real
`TextLibrary` root — **any new test of the memory belongs there, not beside the inline ones.**

Two things follow, both now enforced:

- **The reservation and the builder must agree, or the builder wins** — it is the one that
  drops turns. `ContextBuilder.Reserve` and `ContextBuilder.PersonaLayer` exist so there is one
  answer to "what do the layers around the transcript cost", counted with the same per-message
  overhead, including the persona's frame.
- **Reserving honestly did not make compression a per-turn toll.** The obvious worry — the
  transcript now sits at the budget edge, so every send pays for a summary and an extraction on
  top of the reply — was measured and is wrong: 2 compressions over 8 sends, because a summary
  frees far more room than it occupies. That is a test, not an anecdote.

The BJU turns are still in `Messages`, so a backfill can produce the summaries after the fact —
which is invariant 6 doing its job.

**The spend ledger has now seen real money.** A 146-message BJU session reported
`$0.0553` over 12 calls, 731.8k in / 5.8k out, with `$0.0208` correctly attributed to replies
that were regenerated away. So `usage.cost` and `prompt_tokens_details.cached_tokens` arrive in
the documented shape from a live reply, and the field names are right.

**And the layer order is doing its job — the backend lottery is what costs money.** The same
session's 21% average hid the real shape. Split by host, over fifteen calls at ~61k prompt
tokens each:

| Served by | Calls | Cached |
|---|---|---|
| GMICloud | 5 | 61% |
| Baidu | 2 | 47% |
| DigitalOcean | 4 | 0% |
| Alibaba, Sail Research, SiliconFlow, StreamLake | 1 each | 0% |

Individual calls reached **100%** — 60,416 of 60,611 prompt tokens served from cache. So
nothing ahead of the transcript is moving between turns; the prefix is stable and the ordering
works exactly as designed. Seven hosts appeared in fifteen calls and four of them cache
nothing at all, which on a 60k prompt is most of what a turn costs, decided by a coin flip.
**Pinning or ordering providers is the largest saving available, and it is a request field
rather than a prompt change.** It exists now: `Model:IgnoreProviders` and `PreferProviders`,
sent as OpenRouter's `provider` object and omitted entirely when unset, since that field is
the one part of the request that is not OpenAI's. `airp cost --providers` is what they are
decided from — and its `out/call` column is how a host that answers with token soup rather
than failing is spotted, which happened the same day: `deepinfra` returned 128 tokens a call
against 575 to 791 elsewhere, four replies running, opening with the model's own
`<|begin_of_sentence|>` marker. Slugs are lower-case and a wrong one is dropped in silence,
so the audit's `served by` is the only confirmation that a change took.
