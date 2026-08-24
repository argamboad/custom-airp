# Airp manual

How to use it. For why each decision was made, see the [decision records](adr/README.md).
For how the code is put together — diagrams, call stacks, the schema — start at
[ARCHITECTURE.md](ARCHITECTURE.md).

---

## Contents

1. [Installing](#installing)
2. [First time](#first-time)
3. [Starting a story](#starting-a-story)
4. [The keys](#the-keys)
5. [Commands in the composer](#commands-in-the-composer)
6. [Tuning how it replies](#tuning-how-it-replies)
7. [The memory](#the-memory)
8. [What it costs](#what-it-costs)
9. [Seeing what is going on](#seeing-what-is-going-on)
10. [Importing old transcripts](#importing-old-transcripts)
11. [Playing from Janitor](#playing-from-janitor)
12. [Configuration](#configuration)
13. [When something breaks](#when-something-breaks)
14. [All the commands](#all-the-commands)

---

## Installing

The short way is a [release binary](https://github.com/argamboad/custom-airp/releases): one
self-contained file per platform, with no SDK and no runtime to install. Download the one for
your machine and run it.

```bash
chmod +x airp          # macOS and Linux
./airp
```

macOS refuses an unsigned binary the first time — right-click, Open, and confirm, or
`xattr -d com.apple.quarantine airp`.

Either way you need a terminal that understands ANSI colour: Windows Terminal, iTerm2,
Alacritty, WezTerm and the GNOME and macOS ones all work.

To build it yourself instead, you need the
[.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/argamboad/custom-airp airp
cd airp
dotnet build -c Release
```

```bash
dotnet run --project src/Airp.Terminal -c Release
```

Or install it as a global tool, after which `airp` is on your path:

```bash
dotnet pack src/Airp.Terminal -c Release
dotnet tool install --global --add-source ./src/Airp.Terminal/bin/Release Airp.Terminal
```

### Where everything lives

Everything the application keeps sits under one folder — `%LOCALAPPDATA%\Airp` on Windows,
`~/.local/share/Airp` on Linux and macOS:

| | |
|---|---|
| `airp.db` | the whole history. SQLite, one file, unencrypted |
| `airp.json` | configuration |
| `characters/` `personas/` `openings/` `snippets/` | the library, as plain `.txt` |
| `exports/` | where `X` and `airp export` write, unless you pass `--out` |
| `logs/` | five rolling files, keys and message bodies redacted |
| `secrets/` | the API key, encrypted with your Windows account |

`AIRP_HOME` moves the lot somewhere else, which is how you keep a test install away from your
real one. Relative paths in the configuration resolve against that folder and **not** against
the directory you launched from — otherwise exports would land wherever your shell happened to
be standing. The paths in this manual are written Windows-style; substitute accordingly.

---

## First time

### 1. The model key

Create a key at [openrouter.ai](https://openrouter.ai). **Do not type it on the command line**
— it stays in your shell history:

```bash
airp secret set OPENROUTER_API_KEY
```

On Windows it asks for it with the input hidden and encrypts it with your account (DPAPI).
On Linux and macOS there is no DPAPI, and the command refuses rather than writing your key
in plain text — set it as an environment variable of the same name in your shell profile
instead; the store reads it from there. To check where the key is coming from without
printing it: `airp secret show`.

### 2. Check that it answers

```bash
airp ask "Say something."
```

It returns the reply, the model, which provider served it and how long it took. If this works,
everything else works.

The default is `deepseek/deepseek-v4-flash`. To see others, `airp models --find deepseek`; to
try one without changing anything, `airp ask "…" --model <id>`.

### 3. The library

Characters and personas are **text files**:

```
%LOCALAPPDATA%\Airp\characters\    who the character or the world is
%LOCALAPPDATA%\Airp\personas\      who you are
```

```bash
airp library
```

Shows you what is there and where.

**Start from something that works.** An empty library is a hard place to begin from, so there
is a complete worked example — a character, the persona facing it, an opening, and a snippet:

```bash
airp library --samples
```

It writes four files and never overwrites one you already have, so it is safe to run twice.
Then either press `N` in the terminal, or:

```bash
airp new "Cadgwith Point" --speaker Morwenna --character lighthouse --persona traveller
```

Read `characters/lighthouse.txt` next to this manual's [Starting a story](#starting-a-story)
section: it is the shape every other card here follows, with nothing left in brackets. Note
that the opening is named `lighthouse` too — that filename match is the entire association
the new-chat flow uses to offer it.

To add your own, drop a `.txt` in the folder — or let the client start one for you:

```bash
airp character new elena          # a character file, seeded from the skeleton
airp persona new allan            # a persona file
airp character show elena         # print one
airp character edit elena         # open one in your editor and wait
airp character remove elena       # delete one — refuses if live conversations use it
```

`edit` opens the file in your own editor — `%EDITOR%` if set, Notepad otherwise — and waits,
git-style. Prose deserves a real editor; the terminal's composers are for messages. Saving
the file is enough: every conversation naming it sees the change from its next turn.

The same management lives inside the terminal: press `M` in the chat list for the library —
four shelves (characters, personas, snippets, openings) switched with `←→`, `N` to name and create a new entry (it opens straight in
your editor), `Enter` to edit one, `Del` to remove it behind a confirmation that lists any
conversations still using the name.

The file name is the name you refer to it by. `remove` lists the conversations still naming an
entry and stops, because they would fall back to the default; `--force` deletes anyway. The
skeleton carries the fail-safe rule already phrased as a procedure.

**A name starting with `_` is kept but never listed.** A shelf collects working papers as well
as things to play — a template you copy from, notes towards a character not ready yet — and
`_scenario-template.txt` sits beside the cards it is about without appearing in the manager or
in the pickers that offer something to start. Nothing is hidden from you: `show`, `edit` and
the resolution rule all still find it by name.

**Snippets** are the third shelf: authored prose the composer expands on demand. Write
`snippets/office.txt` today; months later, mid-scene, type `:off`, press `Tab`, and the page
replaces the trigger — still editable before sending. What is sent and stored is exactly what
the composer held, so the memory treats it like any prose you typed: verbatim while recent,
summarised when old, retrieved word-for-word when a later scene touches it. Same verbs:
`airp snippet new|edit|show|remove <name>`. Snippets share the colon trigger with emoji, and
a `character-` name prefix is how you shelve them by owner.

If you always play as the same person, name them once:

```json
{ "Airp": { "defaultPersona": "allan" } }
```

---

## Starting a story

From inside the terminal, press `N` in the chat list: name, speaker, a character and a
persona picked from the library with `<-` `->`, and the opening written in a composer.

As you cycle characters, the panel below fills with that card's world and the persona facing it,
side by side:

```
Name        how it appears in your list
Speaker     who replies
Character   free-use-college   ←→
Persona     allan
────────────────────────────────────────────────────────────────────────────────────────
  Character preview   1–17 of 46   PgUp/PgDn      │  Persona
──────────────────────────────────────────────────┼──────────────────────────────────────
  Freedom Memorial Campus, University of          │  Allan Ramos de Ávila, 31, tall and
  Miskatonic — FMCUM — a dark-academia            │  unhurried, the sort of housemate who
  university upstate, hidden in the woods above   │  notices the kettle is empty before
  its college town of Southampton. Founded 1698,  │  anyone asks.
  and it has never once been ordinary.            │
```

The two are read against each other on purpose: whether this is the right person to walk into
that place is a question about both at once, and stacking them would have made it a scroll.
Each column keeps its own position, and `PgUp` `PgDn` move whichever field has focus — only that
column's heading carries the hint, so you can see which one the keys will move. The world of a
real card runs to forty lines and the paragraph that tells two similarly-named cards apart is
rarely the first one.

The character text is the card's own `=== THE WORLD ===` section, so it is never out of date; a
card without that header shows the top of the file instead. The persona is whichever one the
picker names, and with the picker on `(default: …)` it is the file `defaultPersona` points at —
that is what will really be sent, so that is what it shows.

`Tab` into the opening and the panel becomes the opening's composer instead, with both previews
one `Tab` away. `Ctrl+Enter` creates the chat and drops you straight into it. (`Ctrl+S` does the
same where the terminal passes it through — on Windows the console keeps that chord for flow
control and the application never sees it, which is why the footer names `Ctrl+Enter`.)

The same thing from the command line:

```bash
airp new "Vardhal" --speaker Elena --character elena --persona allan \
  --opening "I'm sitting in the sand, cleaning the knife."
```

| | |
|---|---|
| `"Vardhal"` | how it appears in your list |
| `--speaker` | the name of whoever replies |
| `--character` | a name from the library, or the path to a loose file |
| `--persona` | same |
| `--opening` | the first message, written by you |

**Openings are a shelf of their own**: `openings/<character>.txt`. In the `N` flow, picking a
character pre-fills their opening from the shelf — editable, and never over anything you
typed yourself; the filename-matches-the-character rule is the whole association. Snippets
are a different thing: mid-story prose on a trigger. An opening happens once, at position
zero, and belongs to its character.

**A caution for maximal cards**: a faithful conversion of a site character can run to
~30,000 tokens, and the character layer is sent whole every turn. Raise `contextBudget`
(60,000 covers a 30k card) before playing one, or the world will starve the transcript.

**The `--opening` is worth more than it looks.** A greeting where each character speaks once,
in their own voice, establishes them better than paragraphs describing them — and it stays at
the top of the transcript for the rest of the story.

The conversation stores **the name** of the character, not a copy. If you edit `elena.txt`
later, the change reaches every conversation using it.

**The conversation's header says which card and persona are in play** — `elena · as allan`,
on its own line between the message counts and the cost — resolved exactly as the next turn
will resolve them, so
after weeks inside a story you never have to remember what you set up. Two states are said
out loud in warning colour: a name whose file no longer exists (`elena (missing)`) and a
conversation with `no card` — both mean the model is playing with an empty character layer,
which has happened silently before. `/card` and `/persona` open the full text.

Then play:

```bash
airp
```

To play a turn without the terminal — for a script, or to check something quickly:

```bash
airp send "Where are we right now?"
```

It sends to your most recent conversation unless `--chat` names another, and prints the reply
on its own so a script can read it. **The message goes before the flags**; with the flags
first, name it: `airp send --chat vardhal --text "…"`.

---

## The keys

`?` or `F1` inside gives you the full help. The footer along the bottom shows as many of the
current screen's keys as fit on one line, in the order they are worth knowing; when there are
more than that, the last thing on the line is `? All keys` rather than a legend that wraps.

### Everywhere

| Key | |
|---|---|
| `Ctrl+P` or `:` | Command palette |
| `Ctrl+F` | Search every conversation |
| `Ctrl+R` or `R` | Re-read from the store |
| `F1` or `?` | Help |
| `Esc` | Back one screen |
| `Ctrl+C` or `Q` | Quit |

### If you would rather use `hjkl`

`"keyboard": "Vim"` layers a few bindings on top of the standard ones, **only where you are
navigating**. Inside any text field every printable key types itself, so there is no mode to be
in and none to leave. Arrow keys keep working, and every `Ctrl` chord is the same in both.

| Key | Standard | Vim |
|---|---|---|
| `h` `j` `k` `l` | `L` toggles line numbers | left / down / up / right |
| `G` | Regenerate | jump to the end |
| `n` / `N` | both find the next match | next / **previous** match |
| `u` | — | undo |

That is the whole of it. There is no `gg`, no `dd` and no modal editing; `g` is typed as a
character, `d` opens the diff in both dialects, and redo is `Ctrl+Shift+Z` or `Ctrl+Y`
either way. `L` still toggles line numbers in Vim mode, since `l` is spoken for.

### Moving around

| Key | |
|---|---|
| `↑` `↓` | Move the selection |
| `PgUp` `PgDn` | One page |
| `Home` `End` | First / last |
| `Enter` or `→` | Open |

### In the list

| Key | |
|---|---|
| `Enter` | Open the chat |
| `N` | Start a new one |
| `M` | The library — characters, personas, snippets |
| `F2` | Rename it |
| `Del` | Delete it |
| `/` | Filter as you type |

### Reading it

A conversation is set in a centred column rather than across the whole window —
`transcriptWidthPercent`, sixty by default, `100` for the full width. A maximised terminal
otherwise gives lines of a hundred and eighty characters, and past about ninety the eye loses
the start of the next line on the way back; it is why a newspaper sets narrow columns on a wide
page. A scrollbar sits against the right of the column when there is more than fits.

Each turn opens with the speaker's name on a tinted chip and the time at the far end of the
column, and a hairline separates one turn from the next.

### In the conversation

| Key | |
|---|---|
| `I` or `Enter` | Write a message |
| `↑` `↓` | Previous / next message |
| `/` | Search this conversation |
| `>` | Let it carry on, with nothing from you |
| `G` | Regenerate the last reply |
| `S` | The dials |
| `B` | Branch the story from this message |
| `Del` | Delete this message and everything after it |
| `C` | Copy the message |
| `X` | Export the transcript |

### Taking the story two ways

Put the cursor on a turn, press `B`, and give the copy a name. You now have two conversations:
the one you were playing, untouched, and a new one that ends at that turn and can go somewhere
else entirely.

It is a real copy, not a bookmark. The new story carries the character and persona it names,
all three dials, the inner-thoughts setting, the transcript up to that turn, and the memory
built out of those turns — summaries, the facts that were true at that point, and the
embeddings, so retrieval works from the first turn instead of being bought again.

What it does not carry is anything belonging to turns it does not have:

- **A summary that straddles the branch point is left behind.** It describes a scene that has
  not happened in this version, and the model would be told about it as something established.
- **A fact retired later is still true here.** It was retired by turns this copy does not have,
  so nothing has contradicted it yet.
- **Replies you rerolled away do not come back.** They belong to the original's audit, where
  "why did it say that" gets asked.
- **The bill does not follow.** `Spend` is a record of money actually charged; copying it would
  invent a second bill for calls that happened once. The original keeps its cost, the branch
  starts at zero.

**One thing cannot be rewound: trackers.** A meter stores a number and the turn it last moved,
not the number it held at every turn — so a branch taken from far back carries the meter forward
from a scene the copy has not played. It is visible in `/trackers` and `airp tracker` can set
it back to whatever it should be.

The name is asked for because you are about to have two stories with the same character, the
same persona and the same first hundred turns, and the name is the only thing that will tell
them apart in the list. A numbered suggestion is offered, so Enter is a valid answer.

### How a reply is drawn

Two conventions in the text are read and drawn rather than shown:

| Written | Drawn |
|---|---|
| `*she closes the lid*` | italic and dimmed, asterisks gone |
| `"you are late"` | ordinary text, quotation marks gone |

Straight and curly quotation marks both work. Doubled asterisks are treated the same as
single ones — not a second convention to remember, only that models emit `**` now and then
and a line that did would otherwise show its markers.

A marker that never closes is left exactly as written — a reply cut off mid-action would
otherwise dim everything after it — and a spaced asterisk is not a marker at all, so `2 * 3`
stays arithmetic.

**This is display only.** The stored message keeps every asterisk and every quotation mark,
which is what `C` copies, what `X` exports, and what the next prompt sends. It has to be:
changing the stored wording would break the prefix cache and rewrite what the model actually
said, which the append-only store exists to prevent.

If your console does not draw italics, the dimming still separates action from speech.

The chat list's preview of a conversation's latest message is drawn the same way, through the
same code. Recognising a reply at a glance is the whole point of drawing it, and half of that
would be lost if the list showed the raw markers. The preview stops where its pane does and
marks the cut with `…` — it is there to recognise a chat by, not to read.

While you are writing, a line that starts with `/` is a command to the client rather than
something you said — see [Commands in the composer](#commands-in-the-composer).

Deleting **hides**: the terminal stops showing them and the database keeps them, with a
tombstone. That is deliberate — messages are append-only, so the store refuses to drop one —
and it is why `airp audit` can still answer "why did it say that" about a reply you threw
away. The same is true of a whole conversation: deleting it takes it out of the list and
leaves every word of it on disk.

When you want it gone for real, `airp purge` finishes the job:

```bash
airp purge          # what is still stored, and what erasing it would cost
airp purge --yes    # erase it
```

Without `--yes` it only shows you the deleted conversations and their message counts, and
touches nothing. With it, those conversations and everything they own — messages, summaries,
facts, trackers, and the questions you asked about them — are removed and the database is
vacuumed, so the space is released rather than merely marked free. The spend ledger is kept
on purpose; see [What it costs](#what-it-costs). It never touches a conversation you can still see. There is no undo,
which is the point: the database holds your history in the clear, so "deleted" ought to be
able to mean deleted.

### Regenerating

`G` asks you for a reason, and the reason matters: it is translated into an instruction for
the model, so it is the only thing that makes the second attempt differ from the first.

| | |
|---|---|
| No reason | another reply, without saying why |
| Guide the reply | write it differently — say how in the instructions |
| Bad memory | it contradicted something established earlier |
| Looping | it repeated itself, or the scene stopped moving |
| Writing my actions | it wrote your words or your actions |
| Too short / too long | |
| Wrong format | the prose, dialogue or emphasis came out wrong |
| AI refusing | it declined to continue |

**Whatever you type in the instructions is a direction, not a message.** It is framed before
it goes to the model, saying plainly that it is a note about how to write and not something to
answer. Without that frame a bare line like `Use at least 30 words` gets read as the latest
thing said, and the reply comes back as that line repeated — which is what used to happen.

The reply being replaced is hidden from the prompt before the call, so the model never sees
the wording it is superseding. That is deliberate: shown its own last attempt, a model tends
to write it again. It also means a reason like *Guide the reply* asks for a fresh take rather
than for a comparison against something invisible.

---

## Commands in the composer

A line that starts with `/` is an instruction to the client, not something you said. It never
enters the transcript.

That distinction is the whole point. Typing `(OOC: skip to the evening)` as a message *sends*
it: the model reads it, it is stored for good, it is counted in every prompt after it, it gets
embedded for retrieval, and it may be summarised as something that happened. Messages are
append-only, so there is no taking it back. A command carrying the same words puts them in the
prompt layer they belong in and leaves the story alone.

Type `/` and the names appear on the same strip the emoji and snippets use; `Tab` completes.
A name that is not a command is **refused rather than sent** — a typo would otherwise cost what
the message would have cost and stay in the transcript forever. To send a line that genuinely
begins with a slash, double it: `//ask` sends `/ask`.

### These call the model

| | |
|---|---|
| `/do <direction>` | steer this turn |
| `/ask <question>` | ask about the story out of character |
| `/focus <who>` | hand the next turn to a named character |

**`/do` is two commands wearing one name.** On its own it writes the next beat under your
direction, with no message of yours in the transcript:

```
/do have Mariana leave before he answers
```

With a blank line and prose under it, the direction steers *that* message's reply instead —
the message is stored, the direction is not:

```
/do keep this one short

He sits down without a word.
```

It replaces most of what an OOC line was for: `/do skip to the evening`, `/do less
description`, `/do she should be angrier about this than she is letting on`.

**`/ask` is the one that does not move the story.** It sends the same prompt a real turn would
— the same card, the same persona, the same history and summaries and facts — with a directive
that says to answer as the author rather than act it out. The answer opens in a pane and is
written into no prompt, ever.

Because the prompt is identical up to that last directive, a caching provider charges you
almost nothing for it. And because the answer is stored nowhere, it comes with a trap and a
key for it. Models do not say "the story never mentions that"; asked how far the rehearsal
room is, one will answer confidently, and that answer vanishes when you close the pane —
leaving you playing on a detail the next turn has never heard of.

So the pane offers exactly one thing to do about it. **`F` pins the answer as a fact**, which
goes into the world layer from the next turn on and which the extractor cannot retire. `Esc`
discards it. That turns the trap into the point: asking is how you find out what the story has
implied, and `F` is how you make it binding.

Recall is `/ask`'s weakest use — `/search` beats it at *did this happen*. Where it earns its
keep is judgement:

```
/ask would Mariana be angry if she found out about the video?
/ask what has Nicole not said out loud yet?
/ask what is unresolved in this scene right now?
/ask who has not appeared in a while and should?
```

It can only answer from what is in front of the model that turn: this conversation's card,
persona, recent history, summaries, retrieved memories, facts and meters. Not another
conversation — each chat is its own prompt — and not the stretches that were compressed away
and that retrieval did not surface.

### These only read what is already here

Free, instant, no model call.

| | |
|---|---|
| `/card` | the character definition this conversation resolves |
| `/persona` | who you are playing, **and which file it came from** |
| `/facts` | what is being injected as true right now |
| `/trackers` | the meters and their values |
| `/audit` | what the recent turns cost, layer by layer |
| `/cost` | what this story has cost, and what was rerolled away |
| `/search <words>` | find the turn where something was actually said |
| `/help` | the list |

`/card` and `/persona` resolve exactly the way a turn does — the conversation's own text, then
the file it names, then the default — and say which of the three they used. That line is worth
more than it looks: "I edited the file and it had no effect" is almost always a conversation
holding its own copy, or naming a different file than the one being edited, and nothing else
shows you which.

`/audit` lists the asides too. They are billed and leave no message behind, so leaving them out
is how a per-chat cost quietly stops adding up.

### These write to the conversation

| | |
|---|---|
| `/fact <statement>` | record something as true, pinned |
| `/tracker <name> <value>` | set a meter |

`/fact` is `airp fact add` without leaving the chat, filed under the character's name. Pinned
means the extractor cannot retire it; you still can, with `airp fact retire`.

`/tracker` takes the value as the **last** word, so a meter whose name is two words still
works: `/tracker her patience 40`.

### What is deliberately not here

There is no `/lust`, `/creativity`, `/length` or `/thoughts` — `S` already shows all of those
with the scale's own wording, which beats remembering what `3` means. And there is no
`/skip`, `/narrate`, `/brief` or `/hot`: every one of them is `/do` with canned words, and
`/do keep this one short` reads better than a flag.

If you played on a site whose cards defined their own OOC commands — ourdream's `/image on`,
for instance — those were that engine's plumbing and do nothing here.

---

## Tuning how it replies

The dials live in a **pack** — the application ships one, and `S` inside a conversation shows
whatever the pack in force declares. The originals are still the first four:

```bash
airp settings --chat <id> --lust 3 --length 2 --creativity 2
```

| Dial | What it moves |
|---|---|
| **Creativity** | the model's temperature: 0.6 to 1.4 |
| **Response Length** | the token ceiling: 200 to 2600, and the prompt |
| **Lust** | goes into the prompt, in the scale's own words |
| **Inner thoughts** | a toggle — see below |

Levels run 0 to 4. The shipped pack carries more — pacing, initiative, consequence, prose
balance, register, NPC liveliness, point of view, reply endings, veils, reply language, an
anti-loop penalty — every one unset until you touch it, so a conversation you never adjust
plays exactly as it always has.

```bash
airp dials                              # what exists, and what is in force
airp dials --chat <id> --set pacing=1   # set any dial from the command line
airp dials --chat <id> --clear pacing   # back to the pack's default
```

Scales, toggles and choices are adjusted with `←→` in the `S` view; the typed kinds — veils,
reply language — are set with `--set` (`--set veils=graphic violence,character death`,
`--set language=Spanish`).

### A pack of your own

```bash
airp dials --write
```

writes the shipped pack to `dials.json` beside `airp.json`, comments and all, and from then
on **your file is the pack** — it replaces the shipped one whole, no merging; delete it to go
back. Every dial documents itself in the file: what it does, what values it takes, whether it
is enabled. Two fields worth knowing:

- `enabled: false` hides a dial from the `S` view **without turning it off** — its `default`
  still applies on every prompt. A dial pinned this way is a house rule; a dial you delete
  from the file is gone.
- `default` is what applies when a conversation has not chosen. `null` means the dial says
  nothing at all.

### In your own words

The `Airp:Scales` section still works and still wins for the three original dials — the pack
supplies the controls, this rewords them:

```json
{
  "Airp": {
    "scales": {
      "Lust": {
        "title": "Heat",
        "levels": [
          { "label": "Cold",     "description": "keeps distance, redirects anything physical" },
          { "label": "Warm",     "description": "affectionate, but nothing is spelled out" },
          { "label": "Charged",  "description": "tension acknowledged and acted on, slowly" },
          { "label": "Explicit", "description": "sex is written plainly, at the scene's pace" },
          { "label": "Feral",    "description": "no restraint, no fade to black" }
        ]
      }
    }
  }
}
```

That text is **what you see and what the model receives**. There have to be five levels: with
fewer, it is ignored and the shipped scale returns. You can also replace only the title.

### Inner thoughts

Each character adds one line of what they did **not** say. It lives in the `S` view as the
row under the three dials — a toggle, flipped with `←→` and applied with the rest — or from
the command line:

```bash
airp thoughts on
```

```
Elena: "All right. I believe you."
*She sets the knife aside.*
>Elena's inner thoughts: I don't believe a word of it, but I want to see how far he takes this.
```

Never for you, and if it only repeats what was said out loud, it is omitted. `airp thoughts
off` removes it.

### Meters

Optional, with free-form names:

```bash
airp tracker add ADMIRATION --value 40 \
  --means "Rises when you do something difficult well; falls when you posture" \
  --scale "0 indifferent · 50 respects you · 100 would follow you anywhere" \
  --rule "Cannot exceed 70 while TRUST is below 40"
```

The model draws the meter at the end of each reply and the new value is stored. Without
`--means` the model infers what moves it, and infers differently every turn.

`airp tracker` lists them, `airp tracker remove <name>` removes one.

**An honest warning:** a meter the model can see is a meter the model writes towards. The
scene can start arranging itself to move the number. That is why they are off by default.

---

## The memory

There is nothing to configure. It fires on its own and **only when needed**: a conversation
that fits in the budget does not spend one extra call.

In English with the budget at 32,000 tokens and a small character, that is around 120
messages. Before that, nothing happens.

**Your character card comes out of the same budget**, so a big one moves that number a long
way. A 30,000-token card in a 60,000-token budget leaves the transcript half of what a
3,000-token card would, and compression starts in the first twenty turns rather than the
hundredth. `airp audit` shows the split — the `character` figure against the `history` one —
and it is the first thing to look at if a story starts compressing sooner than you expected.
Raising `contextBudget` is the other lever, at the cost of a larger bill on every turn.

When it does go over:

**The oldest stretch is summarised.** It leaves the prompt, not the conversation — the
messages are still whole in the database and in your transcript.

It goes in **batches**, not one turn at a time. Once the transcript is at the ceiling every
send pushes it over by exactly the exchange you just had, so compressing only the overflow
would run on every turn — and a summary of two messages is not shorter than two messages. One
measured at 0.91×: the summary came out *longer* than the turns it replaced. So compression
takes at least ten messages when it takes any, at most forty, and never reaches into the six
most recent. The cap is there because one summarising call has a fixed output ceiling: a
backlog handed over whole came back as two characters, which is worse than not compressing at
all. A summary too short to be an account of its turns is now refused, and the turns go whole.

**What was compressed is embedded**, and when you write something related to an old moment,
that moment comes back into the prompt in its exact words.

**Facts are extracted**: what ended up being true. With a validity range, so when the story
contradicts one, it is retired rather than accumulated. The batching matters here too — asked
about two messages the extractor returns nothing, correctly, because nothing durable is
established in two messages.

Both are **retried once** if the host answers with nothing. That happens: the same lottery that
produces token soup also returns 200 with an empty body, and unlike a reply — which you see
fail, and can ask for again — a summary failing is a line in the log. One real story lost the
extraction over its first sixty-two messages that way, and nothing will look at those turns
again.

### Making the memory again

The memory is **derived**. Summaries, facts and embeddings all come out of the transcript, and
the transcript is never deleted — so if a version of airp produced them badly, they can be
produced again.

```bash
airp rebuild "BJU"          # what it would replace. Touches nothing
airp rebuild "BJU" --yes    # do it
```

It throws away the summaries and the extracted facts, then works through the transcript exactly
as playing the conversation would have, in the same batches, with the same rules.

**Your own facts are kept.** Anything pinned — `airp fact add`, `/fact`, `F` in the `/ask` pane —
was stated by a person, possibly about something the transcript never mentions. It is not
derived from anything, so a rebuild that took it would be destroying the only copy.

Two things it cannot do. It cannot give back what the first attempt cost: `Spend` records money
actually charged, so the old calls stay in the ledger and the rebuild's own calls are added
beside them. And it costs a call per stretch of its own, which for a long story is a few cents.

Worth doing after an upgrade that changed how the memory works, or when `airp audit` shows
summaries that cover one or two messages each — a sign they were made by a version that
compressed the overflow rather than a batch.

### The facts are yours to edit

```bash
airp fact                                        # see what your story believes
airp fact add "She is allergic to shellfish" --subject Elena
airp fact retire a3f9c201
```

What you write is **pinned**: the extractor cannot retire it, you can.

Know which shelf a truth belongs on: something true of your persona in **every** story — where
you live, what you drive, what you host — goes in the persona file, known from turn zero in
every chat. A fact is for what becomes true in **one** story. And note the one asterisk in the
data model: extracted facts can be rebuilt from the transcript, but a hand-pinned fact the
story never mentioned lives only in `airp.db` — back that file up accordingly.

If a character ever acts against something established — the chef who suddenly trains at the
track — the question is always answerable: `airp audit` shows whether the truth was in the
prompt. If it wasn't, a bad fact crept in (`airp fact retire <id>`). If it was, the model
ignored it that turn: regenerate with **Bad memory**, and check the `served by` column. Useful for correcting
something misread, and for asserting something the conversation never mentioned.

A wrong fact is injected into every prompt until something contradicts it — and since the
character acts on it, nothing does. This is the way out of that loop.

### If you want to watch it work sooner

Drop `contextBudget` to 8000 for a while, send a few messages, look at `airp audit`, then put
it back. You compress it deliberately instead of waiting a hundred messages.

---

## What it costs

Every call that is billed is written to a ledger — one row, with what the provider said it
charged. The figures are not worked out here from a price list: prices change, a router fans
one model across hosts that charge differently, and a cached prefix is discounted, so any
number this client computed itself would drift away from the invoice and never say by how much.

### In the chat

The conversation header carries the running total — with the month's and the day's share in
parentheses, each shown only when it differs from the figure before it, so a story played
in one sitting stays a single number — and `/cost` opens the breakdown: which kinds
of call it went on, how much of the prompt came from cache, and what was spent on replies you
regenerated away.

### Across everything

```bash
airp cost                      # this month, by chat
airp cost --month 2026-07      # a particular month
airp cost --all                # everything, ever
airp cost --chat <id>          # one story
airp cost --json               # the same numbers, for a script
```

```
╭─────────────┬───────┬────────┬───────┬────────┬─────────┬───────────╮
│ chat        │ calls │     in │   out │ cached │    cost │ discarded │
├─────────────┼───────┼────────┼───────┼────────┼─────────┼───────────┤
│ BJU (full)  │    13 │ 689.0k │  5.1k │   83 % │ $0.2023 │         — │
│ Vardhal     │    50 │ 283.0k │ 14.2k │   82 % │ $0.1165 │   $0.0056 │
╰─────────────┴───────┴────────┴───────┴────────┴─────────┴───────────╯
August 2026: $0.3188 over 63 call(s), 972.0k in / 19.3k out, 82 % cached.
  replies $0.3038 (52)  ·  questions $0.0030 (5)  ·  compression $0.0120 (6)
  $0.0056 of that went on replies that were regenerated away.
```

Two columns matter more than the total.

**Discarded** is what was paid for replies you then rerolled. Regenerating hides the old reply
and asks for another; the first one was still generated and still charged. It is the one line
of spending that bought nothing, and the only one you can do anything about directly.

**Cached** is the share of the prompt the provider did not have to read again. That is the prompt's layer order doing its job or not doing it — a low share on a long
conversation means either something before the transcript is changing between turns, or the
host serving you that day does not cache at all.

### What is and is not in the number

Counted: replies, `/ask` questions, compression, and fact extraction. The last two fire on
their own, without you asking for anything, and they are the ones worth watching as a story
gets long.

Not counted: embeddings. The whole corpus costs under a cent, and the report says so rather
than pretending completeness.

A call the API returns no price for is reported as *unpriced* rather than as zero — the total
says it is a floor. Zero and "never said" are different facts.

### The ledger survives a purge

`airp purge` erases conversations, messages, summaries, facts, trackers and questions. It
deliberately **keeps** the spend rows, and says how many. They hold no story text — model
names, token counts and money — so keeping them takes nothing back from the erasure, and
dropping them would quietly make every report covering that month wrong. A purged story still
appears in `airp cost`, under the name `(purged)`.
---

## Seeing what is going on

```bash
airp audit
```

Under the conversation's name it prints its identifier — the proxy setup needs it — and for
each reply: when it arrived, which provider served it, the estimated tokens against the
actually reported ones, and the prompt broken down layer by layer:

```
character 380 · persona 340 · world 210 · summaries 1200 · history 24500 · total 26630/32000
```

Below that, the live facts and the summaries with the turn range each one covers.

Replies you rerolled appear **struck through, not absent**. "Why did it say that" is almost
always asked about a reply you threw away.

---

## Importing old transcripts

`airp import` reads ourdream.ai export files — JSON transcripts on disk — into the store:

```bash
airp import <path-to-exports>
```

It is safe to run twice; it skips what it already has. The exports do not carry the character
definition, so you can attach one at import with `--character <name>`, or leave it off — with
thousands of words of the character speaking in their own transcript, the model reads them
fairly well.

The exports themselves came from the ourdream client, which is its own application in its own
repository. This one only ever reads the files.

---

## Playing from Janitor

Optional. It is for playing **your local conversations** from your phone, with Janitor's
interface but your memory and your model.

```bash
airp secret set AIRP_PROXY_TOKEN     # your own token, different from the model key
dotnet run --project src/Airp.Proxy --urls http://localhost:5290
```

Then a TLS tunnel — `cloudflared tunnel --url http://localhost:5290` — and in Janitor you
point the Proxy URL at `https://whatever/v1/chat/completions`, with that token as the API key.

In the Custom Prompt put `[[rp:<id>]]` with the conversation's id, which `airp audit` prints
under the conversation's name. Without it the proxy tries to recognise the conversation by its
character or by how the transcript opens, and **if it cannot, it returns an error rather than
guessing** — writing a turn into the wrong conversation is permanent.

The proxy does not start without a token configured. Behind it is a database with all your
conversations in the clear, reachable from wherever the tunnel reaches.

Janitor sends its own truncated history; the proxy **discards it** and builds the prompt from
your store.

---

## Configuration

`%LOCALAPPDATA%\Airp\airp.json` — in the application data directory, not beside the
binary, so reinstalling the tool never touches it. To see what is in effect: `airp config`.

**`airp config --rewrite` brings an old file up to date.** It is the only thing that looks inside
a file that already exists: the file is created once with defaults and then left alone, so a
settings file written by an earlier version keeps its shape through any number of reinstalls and
never gains the keys added since. The rewrite is purely additive — anything already set keeps its
value exactly, missing keys arrive with their defaults, and the `// one of:` comments are put
back. Those comments are regenerated on every write rather than preserved, since the file is
parsed to a tree and written back from it; a comment you add by hand elsewhere is lost the next
time the file is written.

```json
{
  "Airp": {
    "defaultPersona": "allan",
    // one of: Dark, Light, HighContrast, Monochrome
    "theme": "Dark",
    // one of: Standard, Vim
    "keyboard": "Standard",
    "transcriptWidthPercent": 60,
    "model": {
      "name": "deepseek/deepseek-v4-flash",
      "contextBudget": 32000,
      "maxTokens": 1024,
      "temperature": 1.0,
      "recallCount": 4,
      "recallThreshold": 0.35
    }
  }
}
```

| | |
|---|---|
| `theme` | Dark, Light, HighContrast, Monochrome |
| `keyboard` | Standard or Vim |
| `transcriptWidthPercent` | how much of the window a conversation occupies, centred. 100 fills it. 30–100 |
| `contextBudget` | the prompt ceiling in tokens. Higher = more verbatim history, more expensive |
| `maxTokens` | maximum length of a reply, when the dial is not in charge |
| `recallCount` | how many old moments retrieval may bring back |
| `recallThreshold` | how similar something has to be to come back |
| `backgroundModel` | model for summarising and extracting facts. Empty = the same one that replies |
| `messageCharacterLimit` | refuse to send a message longer than this. 0 = no limit |
| `ignoreProviders` | hosts never to route to — see [Choosing which host serves you](#choosing-which-host-serves-you) |
| `preferProviders` | hosts to try first, in order |
| `allowProviderFallbacks` | `false` makes `preferProviders` a restriction rather than a preference |
| `embeddingBaseUrl` | where retrieval's embeddings come from. Empty = wherever the replies do |
| `embeddingApiKeyName` | the secret for that endpoint. Empty = the same one |

`AIRP_*` variables override the configuration: `AIRP_Model__Name=…`. Ones ending in `_KEY` or
`_TOKEN` **never** enter the configuration, so that no dump can print them.

### Choosing which host serves you

OpenRouter fans one model across many machines, and **they are not interchangeable.** They
differ in price, in whether they cache your prompt, in what they are willing to write — and
occasionally in whether they work at all. Measured in one real session, same model, same
conversation, minutes apart:

```
airp cost --providers
```

```
│ served by     │ slug         │ calls │     in │ cached │ out/call │    cost │
│ GMICloud      │ gmicloud     │     5 │ 305.0k │   61 % │      575 │ $0.0137 │
│ Baidu         │ baidu        │     2 │ 122.1k │   47 % │      791 │ $0.0064 │
│ DigitalOcean  │ digitalocean │     5 │ 305.2k │    0 % │      291 │ $0.0210 │
│ DeepInfra     │ deepinfra    │     5 │ 305.3k │  100 % │      128 │ $0.0057 │
```

**`out/call` is the column that catches a broken host.** DeepInfra there was returning token
soup beginning with the model's own start-of-sequence marker — its serving stack applying the
chat template wrongly. It does not fail the request; it answers, charges you, and hands back a
hundred tokens of nonsense where the others give eight hundred. Averaged per call that stands
out instantly.

**`cached` decides most of the bill.** On a 60,000-token prompt the difference between 61% and
0% is most of what a turn costs, and it is a coin flip unless you say otherwise.

Two settings, both lists of provider **slugs** — the lower-case name in the table:

```json
"model": {
  "ignoreProviders": ["deepinfra"],
  "preferProviders": ["gmicloud", "baidu"]
}
```

`ignoreProviders` is never routed to again. `preferProviders` is tried in order, with the rest
still available behind it — add `"allowProviderFallbacks": false` to make it a restriction
rather than a preference.

**A slug that matches no host is dropped without complaint.** Two checks, and you want both.
`airp config` prints the lists back as they will be sent, which catches a setting written into
the wrong place in the file:

```
│ Denied hosts       │ deepinfra                               │
│ Preferred hosts    │ gmicloud, baidu, coreweave, siliconflow  │
```

That only proves the file was read. Whether the router recognised the names is a different
question, so play a turn and check what `airp audit` says served it. If a name was wrong you
will see the same host you meant to avoid.

One thing worth thinking about before pinning: choosing a host is also choosing who is willing
to write your scenes. The cheapest one that caches is not automatically the one that will
carry a scene where you want it to go.

### Using a provider other than OpenRouter

**Only OpenRouter has actually been tested.** Everything here should work with any
OpenAI-compatible API — the request this client sends is plain OpenAI — but nobody has run it
in anger anywhere else. If you do, the project would like to hear about it.

```json
"model": {
  "baseUrl": "https://api.deepseek.com/v1",
  "name": "deepseek-chat",
  "apiKeyName": "DEEPSEEK_API_KEY"
}
```

Two things go quiet on a provider that is not OpenRouter, and neither stops you playing:

- **`airp cost` stops totalling.** The price of a call is something OpenRouter reports and
  most others do not. Calls are counted as *unpriced* and the total says it is a floor,
  rather than claiming everything was free.
- **The audit's *served by* column empties**, since only a router has several hosts to name.

**Embeddings may need their own address.** DeepSeek has no `/embeddings` endpoint at all, so
pointing everything at it would leave the memory unable to recall specific old moments. Split
them:

```json
"model": {
  "baseUrl": "https://api.deepseek.com/v1",
  "embeddingBaseUrl": "https://openrouter.ai/api/v1",
  "embeddingApiKeyName": "OPENROUTER_API_KEY"
}
```

Both fall back to the main ones when unset, so a single-service setup needs neither.

---

## When something breaks

**The terminal will not start** — it needs an interactive console. If you are redirecting
output, use `airp config` or `airp audit`.

**"Unknown command"** — the command comes first and the flags after: `airp send "hi" --chat x`,
not `airp --chat x send "hi"`.

**"No API key is configured"** — `airp secret set OPENROUTER_API_KEY` on Windows; on Linux
and macOS, export `OPENROUTER_API_KEY` in your shell profile.

**"The account is out of credit"** — top up at OpenRouter.

**"Your message was kept — do not send it again"** — the model failed but your message is
stored. Refresh; if the reply never arrives, send the same text again — it will not be stored
twice. Some of the providers OpenRouter uses intermittently return an empty reply; this is
what that looks like.

**The character replies oddly or out of tone** — look at `airp audit`. The provider changes
between requests and some of them write differently. Also check whether a fact was badly
extracted, with `airp fact`, and retire it.

**A scene cuts off halfway** — the length dial or `maxTokens` is too low. The audit flags it.

**One turn took far longer than usual** — that was the turn where compression happened. It
happens once per stretch, not on every message.

**The reply is a wall of unrelated words**, sometimes opening with something like
`<|begin_of_sentence|>` — that is one broken host, not the model and not your machine. Check
the audit's *served by* for the turn, then deny it: `"ignoreProviders": ["thatslug"]`. See
[Choosing which host serves you](#choosing-which-host-serves-you). Its `out/call` figure in
`airp cost --providers` gives it away — a couple of hundred tokens a call where the working
hosts write six or eight.

**`airp fact` is empty after a long story** — nothing has been compressed yet, and facts are
only extracted from turns on their way out of the prompt. `airp audit` shows whether the
transcript has reached the budget. If it plainly has and there are still no summaries, that is
a bug worth reporting.

**The facts read oddly, or the summaries cover one or two messages each** — they were made by
an older version. `airp rebuild <chat>` shows what it would replace and
[Making the memory again](#making-the-memory-again) explains what survives.

**Nothing changed after editing `airp.json`** — `airp config` prints what was actually read.
A settings block under the wrong parent, or a provider slug that matches no host, both present
as nothing having happened.

**The model writes your actions** — it is the most common failure of any character sheet. Put
it as a procedure, not a prohibition: *"if a moment requires assuming what the user wants,
stop there and hand the scene back to them"*. It is in the template.

---

## All the commands

```bash
airp help
```

That lists the verbs you type at a shell. The ones you type inside a conversation, starting
with a slash, are their own thing — `/help` inside the composer lists those, and
[Commands in the composer](#commands-in-the-composer) explains them.
