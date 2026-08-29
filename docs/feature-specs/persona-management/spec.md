# Feature Spec: Persona management

## Status
**Draft**

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-07-02   |
| Last Updated | 2026-07-02   |

## Purpose

Give a place to discover, create, edit, and organise reviewer personas across three
scopes — the personas that ship with the tool, a personal library on disk, and the
personas committed to a repository — and a bounded, explicit way to import personas
authored for other tools. The goal is a curated library, not a filesystem crawl.

## Prototype

[`mockups/personas.html`](mockups/personas.html) — the persona library screen (scope
tabs, source badges, edit/duplicate/promote, and the `Import persona` entry) — and
[`mockups/persona-editor.html`](mockups/persona-editor.html), the editor shown confirming
an import from a Claude Code subagent. Self-contained HTML; open directly in a browser.

## Affected layers

| Project / area | Change type |
|---|---|
| `PeanutGallery.Core` | possible new value: a `scope` tag on a persona/config source (built-in / user / repo); resolution stays a pure fold |
| `PeanutGallery.Cli` | possible `personas` verb extensions (list by scope, import) |
| `PeanutGallery.Engine` | none |
| Desktop shell | Personas screen: library grouped by scope, editor, importer |
| Config IO (shell) | load/merge personas from bundled + user-dir + repo sources |

> Reminder (ADR-0001): scope *resolution* (which persona wins, merged list) is a pure
> fold; *reading files* from the user dir / repo is a shell concern.

## Scope stack

Personas layer by scope, like config precedence:

| Scope | Lives in | Usable by |
|---|---|---|
| **Built-in** | bundled with the tool (`action/default.json` personas) | everyone, read-only |
| **My library** | a user dir the tool owns, e.g. `~/.config/peanut-gallery/personas/` | app-run + one-shot reviews, any repo |
| **This repo** | committed in the repo (`.github/peanut-gallery.json`) | **CI** + everyone on the team |

Because CI runs from the committed config, **CI can only use repo-scoped personas**;
library personas are usable by the app/one-shot executors. Moving a persona *up* toward
the repo (library → repo) is a pull request; moving *down* (repo → library) is a free local
copy — the same promotion rule as [ADR-0002](../../adr/0002-review-executors-and-workflow-file-boundary.md) Decision 4.

## Discovery vs import

**No filesystem discovery.** The tool does not scan the home directory for third-party
agent folders (Claude, Cursor, OpenAI, ad-hoc `.agents`). There is no standard format,
the false-positive and privacy costs are high, and auto-import yields half-mapped junk.

**Explicit import instead.** An `Import persona` action takes a file (or pasted text),
maps it into a persona, and **pre-fills the editor for the user to confirm** — a review
step, never a silent bulk slurp:
- **First-class: Claude Code subagent** (`.claude/agents/<name>.md` — YAML frontmatter
  `name` / `description` / optional `model` / `tools`, plus a markdown system-prompt body).
  Structured and common; maps cleanly (body → `systemPrompt`, `tools` present → agent tier,
  `model` → a provider/model pick, `name` → `name`).
- **Generic: any markdown / text file** → content becomes the `systemPrompt`; the rest is
  filled in the editor.

**Bounded, opt-in repo detection.** If the *currently selected repo* contains a
`.claude/agents/` directory, offer "Found N Claude subagents here — import?" — one repo,
one known format, opt-in. This is not a home-dir crawl.

## Requirements
- [ ] List personas grouped/filtered by scope, each with a source badge and model/lens/tier.
- [ ] Create / edit / duplicate / delete personas in the library and repo scopes (built-in is read-only).
- [ ] Promote a library persona to a repo (opens a PR); copy a repo persona to the library (local).
- [ ] Import a persona from a Claude Code subagent file or pasted markdown, into a pre-filled editor.
- [ ] Optionally detect and offer to import `.claude/agents/*` from the selected repo.
- [ ] Never scan the home directory for third-party agent folders.

## Core changes

- Optionally add a `scope` marker so a resolved persona knows its source; **resolution
  remains a pure fold** over the merged (built-in ∪ user ∪ repo) set with defined
  precedence and de-duplication by id. No IO in the core.

## Shell changes

- Config IO loads personas from the bundled default, the user library dir, and the repo's
  committed config, and merges them.
- Import mappers (Claude-subagent parser → persona; markdown → persona) live in the shell.
- Promotion-to-repo generates a config change and opens a PR (ADR-0002).

## Config / contract changes

- A user-library persona store (files under the user dir) — format is the same persona
  JSON shape the config already uses (or one file per persona).
- No change to the persona value shape itself, beyond the optional `scope` tag.

## Out of scope

- Auto-discovery / home-dir scanning (explicitly rejected — see `adr.md`).
- Importing OpenAI custom-GPT / Assistants formats (possible later; not first-class now).
- Sharing a library between machines / cloud sync.

## Open questions
- [ ] User library on-disk layout: one JSON per persona vs a single `personas.json`? — *Owner: Charles*
- [ ] Should the same import machinery back a CLI `personas import` verb? — *Owner: Charles*

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Founding ADR | [`adr/0001-functional-core-multi-shell.md`](../../adr/0001-functional-core-multi-shell.md) |
| Executors ADR (promotion rule) | [`adr/0002-review-executors-and-workflow-file-boundary.md`](../../adr/0002-review-executors-and-workflow-file-boundary.md) |
| Sibling ADR (the how) | [`adr.md`](adr.md) |
| Desktop GUI | [`../desktop-gui/spec.md`](../desktop-gui/spec.md) |
