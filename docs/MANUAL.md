# Airp manual

How to use it. For why each decision was made, see `CLAUDE.md`.

---

## Contents

1. [Installing](#installing)
2. [First time](#first-time)
3. [Starting a story](#starting-a-story)
4. [The keys](#the-keys)
5. [Tuning how it replies](#tuning-how-it-replies)
6. [The memory](#the-memory)
7. [Seeing what is going on](#seeing-what-is-going-on)
8. [Importing old transcripts](#importing-old-transcripts)
9. [Playing from Janitor](#playing-from-janitor)
10. [Configuration](#configuration)
11. [When something breaks](#when-something-breaks)

---

## Installing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a terminal that
understands ANSI colour — Windows Terminal, iTerm2, Alacritty, WezTerm and the GNOME and macOS
ones all work.

```bash
git clone <your-fork> airp
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

Everything the application keeps lives in `%LOCALAPPDATA%\Airp` on Windows, or
`~/.local/share/Airp` on Linux and macOS — the database, the library, the logs, the
configuration. `AIRP_HOME` points it somewhere else, which is what the QA harness uses to
stay out of your data. The paths in this manual are written Windows-style; substitute
accordingly.

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

Shows you what is there and where. To add something, drop a `.txt` in the folder — or let the
client start one for you:

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
`Ctrl+Enter` creates it and drops you straight into the conversation. (`Ctrl+S` does the
same where the terminal passes it through — on Windows the console keeps that chord for
flow control and the application never sees it, which is why the footer names `Ctrl+Enter`.)

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

`?` or `F1` inside gives you the full help.

### Everywhere

| Key | |
|---|---|
| `Ctrl+P` or `:` | Command palette |
| `Ctrl+F` | Search every conversation |
| `Ctrl+R` or `R` | Re-read from the store |
| `F1` or `?` | Help |
| `Esc` | Back one screen |
| `Ctrl+C` or `Q` | Quit |

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

### In the conversation

| Key | |
|---|---|
| `I` or `Enter` | Write a message |
| `↑` `↓` | Previous / next message |
| `/` | Search this conversation |
| `>` | Let it carry on, with nothing from you |
| `G` | Regenerate the last reply |
| `S` | The dials |
| `Del` | Delete this message and everything after it |
| `C` | Copy the message |
| `X` | Export the transcript |

Deleting **hides**: the terminal stops showing them and the database keeps them, with a
tombstone. Nothing said is ever truly erased.

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

---

## Tuning how it replies

Three dials per conversation, with `S` inside or from outside:

```bash
airp settings --chat <id> --lust 3 --length 2 --creativity 2
```

| Dial | What it moves |
|---|---|
| **Creativity** | the model's temperature: 0.6 to 1.4 |
| **Response Length** | the token ceiling: 200 to 2600 |
| **Lust** | goes into the prompt, in the scale's own words |

Levels run 0 to 4.

### In your own words

The shipped wording can be replaced:

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

In English with the budget at 32,000 tokens, that is around 120 messages. Before that,
nothing happens.

When it does go over:

**The oldest stretch is summarised.** It leaves the prompt, not the conversation — the
messages are still whole in the database and in your transcript.

**What was compressed is embedded**, and when you write something related to an old moment,
that moment comes back into the prompt in its exact words.

**Facts are extracted**: what ended up being true. With a validity range, so when the story
contradicts one, it is retired rather than accumulated.

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

`%LOCALAPPDATA%\Airp\airp.json`. To see what is in effect: `airp config`.

```json
{
  "Airp": {
    "defaultPersona": "allan",
    "theme": "Dark",
    "keyboard": "Standard",
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
| `contextBudget` | the prompt ceiling in tokens. Higher = more verbatim history, more expensive |
| `maxTokens` | maximum length of a reply, when the dial is not in charge |
| `recallCount` | how many old moments retrieval may bring back |
| `recallThreshold` | how similar something has to be to come back |
| `backgroundModel` | model for summarising and extracting facts. Empty = the same one that replies |
| `messageCharacterLimit` | refuse to send a message longer than this. 0 = no limit |

`AIRP_*` variables override the configuration: `AIRP_Model__Name=…`. Ones ending in `_KEY` or
`_TOKEN` **never** enter the configuration, so that no dump can print them.

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

**The model writes your actions** — it is the most common failure of any character sheet. Put
it as a procedure, not a prohibition: *"if a moment requires assuming what the user wants,
stop there and hand the scene back to them"*. It is in the template.

---

## All the commands

```bash
airp help
```
