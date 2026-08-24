# Flows

The call stacks that matter, one sequence diagram per story. Line-level detail for the first
of them lives in [CALLSTACK.md](CALLSTACK.md); the classes are mapped in
[ARCHITECTURE.md](ARCHITECTURE.md).

---

## 1. A send, end to end

The path every turn takes: keypress → persist the user's turn → compose (which may compress)
→ model → persist reply + audit + spend row.

```mermaid
sequenceDiagram
    autonumber
    actor R as Reader
    participant CV as ConversationView
    participant CS as ConversationService
    participant P as LocalConversationProvider
    participant DB as AirpDbContext
    participant SUM as ConversationSummariser
    participant RET as MemoryRetriever
    participant LM as OpenRouterClient

    R->>CV: Enter in the composer
    CV->>CS: SendAsync(id, text)
    CS->>P: SendAsync(id, text)
    P->>DB: anchor = max Sequence of live assistant rows
    P->>P: hash = SHA256(conversationId, anchor, text, instruction)
    P->>DB: existing row with this RequestHash?
    alt already sent and answered
        P-->>CS: stored exchange — no second charge
    else stored but never answered
        Note over P: retry against the row already there
    else new
        P->>DB: INSERT user turn (invariant 2 — before the model)
    end
    P->>P: ComposeAsync
    activate P
    P->>DB: read visible history, live facts, trackers, dial choices
    P->>P: resolve character and persona (own text → file → default)
    P->>P: DialEngine — dial choices + pack → directives text<br/>and sampler overrides (temperature, ceiling, penalty)
    P->>SUM: PrepareAsync(resolved layers)
    Note over SUM: may compress — see flow 2
    P->>DB: re-read live facts (extraction may have just run)
    P->>RET: RecallAsync (see flow 3)
    P->>P: LocalPrompt.Build → ContextBuilder.Build
    deactivate P
    P->>LM: CompleteAsync(messages, model, temperature, ceiling)
    LM-->>P: ModelReply (text, provider, usage.cost, cached_tokens)
    P->>P: Trackers.Absorb(reply text)
    P->>DB: INSERT Spend row (whatever becomes of the reply)
    P->>DB: INSERT assistant turn + ContextAudit
    P-->>CV: [sent, reply]
```

If the model fails, the user's turn is **not** undone: the caller gets a
`ReplyMissingException` carrying it, with the hint *do not send it again* — the retry finds the
unanswered row by its `RequestHash` and asks again instead of storing the sentence twice.

---

## 2. Compression firing

Runs inside `ComposeAsync`, before the prompt is built — never after, because the builder's
only tool for an over-budget prompt is dropping turns.

```mermaid
sequenceDiagram
    autonumber
    participant P as LocalConversationProvider
    participant SUM as ConversationSummariser
    participant FE as FactExtractor
    participant BG as Background
    participant LM as OpenRouterClient
    participant DB as AirpDbContext

    P->>SUM: PrepareAsync(history, resolved character/persona, directives, world, trackers)
    SUM->>DB: existing summaries → covered watermark
    SUM->>SUM: reserved = ContextBuilder.Reserve(all fixed layers)<br/>+ retrieval estimate + MaxTokens + 200
    SUM->>SUM: allowance = ContextBudget − reserved<br/>walk back from newest turn until spent
    SUM->>SUM: Worthwhile(overflow): floor 10, cap 40,<br/>never the 6 most recent
    alt nothing overflows
        SUM-->>P: summaries + whole recent history
    else stretch to compress
        SUM->>BG: CompleteAsync(ModelTask.Summary, T=0.3, ceiling 1200)
        BG->>LM: attempt 1
        alt retryable failure (no status / 200-no-content / 408 / 429 / 5xx)
            BG->>LM: attempt 2 (router usually picks another host)
        end
        LM-->>SUM: reply
        SUM->>DB: INSERT Spend row (before judging the reply)
        alt empty, or shorter than Credible = source/60
            SUM-->>P: CompressionFailed = true
            Note over P: budget := int.MaxValue —<br/>send whole, go over budget,<br/>never drop
        else credible summary
            SUM->>DB: INSERT SummaryRecord [From..To]
            SUM->>FE: UpdateAsync(same stretch)
            FE->>BG: CompleteAsync(ModelTask.Facts, T=0.2, ceiling 4000)
            BG->>LM: JSON: facts to add, ids to retire
            FE->>DB: INSERT new FactRecords<br/>set ValidToSequence on retired (never pinned)
            FE->>DB: INSERT Spend row
            SUM-->>P: summaries + recent tail
        end
    end
```

The summariser and extractor read the stretch through one renderer, `Transcript.Render`, which
names the reader by their persona — the extractor files facts under whatever label it sees,
and `User` once split one person into two subjects.

---

## 3. Retrieval, every turn

```mermaid
sequenceDiagram
    autonumber
    participant P as LocalConversationProvider
    participant RET as MemoryRetriever
    participant EMB as OpenRouterEmbeddingClient
    participant DB as AirpDbContext

    P->>P: compressedUpTo = first recent turn − 1
    alt no embedding client, nothing compressed, or no user turn to query with
        P->>P: memories = []
    else
        P->>RET: BackfillAsync(up to compressedUpTo)
        RET->>DB: aged-out turns with Embedding == null (≤128)
        RET->>EMB: EmbedAsync(batch)
        RET->>DB: store vectors (BLOB of floats)
        P->>RET: RecallAsync(query = last user turn)
        RET->>EMB: EmbedAsync([query])
        RET->>RET: cosine vs. every candidate<br/>≥ RecallThreshold, top RecallCount,<br/>then back into transcript order
        RET-->>P: "Earlier in this conversation:" + lines
    end
```

Failures degrade to an empty list and a log line — retrieval improves a prompt; it is never
the reason a reader cannot get a reply. Only already-compressed turns are candidates: recent
ones are sent whole anyway.

---

## 4. Regenerate

```mermaid
sequenceDiagram
    autonumber
    actor R as Reader
    participant RV as RegenerateView
    participant P as LocalConversationProvider
    participant DB as AirpDbContext
    participant LM as OpenRouterClient

    R->>RV: pick a reason, add instructions
    RV->>P: RegenerateAsync(id, reason, instructions)
    P->>DB: newest live message — must be an assistant turn
    P->>DB: tombstone it (DeletedAtUtc = now)
    Note over P,DB: hidden BEFORE the call, or the prompt would end<br/>on the very reply being rewritten
    P->>P: ReplyAsync with LocalPrompt.RegenerateDirective
    Note over P: the directive is framed — what the note is,<br/>and that the reply is the scene, never the note echoed
    alt model answered
        P->>DB: INSERT new reply + Spend row
    else model failed
        P->>DB: un-tombstone the old reply
        Note over P: a failed regenerate must not eat<br/>the reply the reader had
    end
```

The old wording stays in the database under its tombstone — `airp audit` still shows it,
because "why did it say that" is almost always asked about a reply that was thrown away. The
spend report reads discardedness **from the tombstone at report time**, never stores it.

---

## 5. `airp rebuild` — invariant 6, spent deliberately

```mermaid
sequenceDiagram
    autonumber
    actor O as Owner
    participant P as LocalConversationProvider
    participant DB as AirpDbContext

    O->>P: RebuildMemoryAsync(id)  [after --yes]
    P->>DB: DELETE summaries, DELETE extracted facts
    Note over DB: pinned facts kept — the one thing<br/>derived from nothing
    loop until a pass writes no new summary (cap 200)
        P->>P: ComposeAsync(instruction: null)
        Note over P: the ordinary send path, not a second<br/>implementation — a rebuild cannot produce<br/>a memory the app never would
        P->>DB: summaries/facts accrue as flow 2 fires
    end
    P-->>O: counts: removed, written, extracted, covered
```

The ledger is added to, never reset — the first attempt's calls happened. Without `--yes` the
command prints what it would replace and touches nothing.

---

## 6. Branch at the cursor (`B`)

```mermaid
sequenceDiagram
    autonumber
    participant CV as ConversationView
    participant P as LocalConversationProvider
    participant DB as AirpDbContext

    CV->>P: BranchAsync(id, throughMessageId, name)
    P->>DB: source row + branch point sequence
    P->>DB: new ConversationRecord (character, persona, dials, model copied)
    P->>DB: copy visible messages ≤ point<br/>(embeddings carried — same text, same vector)
    P->>DB: copy summaries wholly inside the branch (ToSequence ≤ point)
    P->>DB: copy facts with ValidFrom ≤ point<br/>(a retirement AFTER the point is undone)
    P->>DB: copy trackers at their current value<br/>(the one thing that cannot be rewound)
    P->>DB: copy asides ≤ point
    Note over P,DB: left behind on purpose: Spend (a ledger must not<br/>duplicate), RequestHash (hashes the source id),<br/>tombstoned replies (the original's audit)
```

---

## 7. The proxy — playing from a third-party front end

```mermaid
sequenceDiagram
    autonumber
    participant J as Front end (Janitor)
    participant PX as Airp.Proxy
    participant SR as SessionResolver
    participant P as LocalConversationProvider

    J->>PX: POST /v1/chat/completions + Bearer
    PX->>PX: constant-time token check — 401 on mismatch
    PX->>P: ListAsync + first stored user turn per chat
    PX->>SR: Resolve(full prompt, first user turn, chats, openings)
    Note over SR: 1. [[rp:id]] tag — exact<br/>2. speaker name — unique or ambiguous<br/>3. opening prefix — normalised first 80 chars
    alt no unambiguous match
        PX-->>J: 404 with instructions (never guesses —<br/>a wrong write is permanent)
    else resolved
        PX->>P: SendAsync(id, newest user turn only)
        Note over P: flows 1–3 run exactly as from the terminal —<br/>the front end's truncated history is discarded
        alt stream: true
            PX-->>J: finished reply chunked as SSE
        else
            PX-->>J: one OpenAI-shaped completion
        end
    end
```

---

## 8. Message lifecycle

```mermaid
stateDiagram-v2
    [*] --> Persisted : INSERT — user turn before the model, reply after
    Persisted --> InPrompt : visible, within budget
    InPrompt --> Compressed : stretch covered by a summary, facts extracted
    Compressed --> Embedded : backfill on a later turn
    Embedded --> Recalled : cosine ≥ threshold for a query
    Recalled --> Embedded
    Persisted --> Tombstoned : reroll or delete-from sets DeletedAtUtc
    Tombstoned --> Persisted : failed regenerate restores
    Tombstoned --> [*] : purge — the one guarded erasure
    note right of Persisted
        Text never edited, row never deleted.
        AirpDbContext.SaveChanges refuses both.
    end note
```

---

## 9. TUI navigation

The shell holds a stack; every arrow pushes, `Esc` pops.

```mermaid
flowchart LR
    CL[ChatListView]
    CV[ConversationView]
    NC[NewChatView]
    LIB[LibraryView]
    ST[ChatSettingsView]
    RG[RegenerateView]
    AV[AskView]
    SV[SearchView]
    EX[ExportView]
    CP[CommandPaletteView]
    HP[HelpView]
    TP[TextPaneView]

    CL -- "Enter" --> CV
    CL -- "N new chat" --> NC
    CL -- "M library" --> LIB
    CL -- "Ctrl+F" --> SV
    CV -- "S dials" --> ST
    CV -- "G regenerate" --> RG
    CV -- "/ask → pane" --> AV
    CV -- "E export" --> EX
    CV -- "/card /persona /audit …" --> TP
    CL -- "palette" --> CP
    CV -- "?" --> HP
```

Twelve slash commands live in the composer (`/do`, `/ask`, `/focus` billed; `/card`,
`/persona`, `/facts`, `/trackers`, `/audit`, `/cost`, `/search`, `/help` free; `/fact`,
`/tracker` write). An unrecognised command is refused, never sent; `//` escapes a literal
slash. `F` in the ask pane promotes an answer to a pinned fact.
