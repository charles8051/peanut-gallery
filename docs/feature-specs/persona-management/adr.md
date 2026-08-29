# ADR — Persona management

**Status:** Draft
**Date:** 2026-07-02
**Deciders:** Charles Lee
**Feature:** [Persona management](spec.md)

---

## Context

Personas need somewhere to live and a way to be discovered. Two facts shape the design:
some personas belong to a repo (versioned, team-shared, and the only ones CI can use),
while others are personal and reused across projects; and many developers already have
prompt libraries authored for other tools (Claude Code subagents, Cursor rules, OpenAI
custom GPTs, ad-hoc `.agents` folders). The tension is between reusing that investment and
not turning the tool into a filesystem crawler that guesses at a dozen incompatible
formats.

---

## Decision 1 — A three-scope stack (built-in / user library / repo) with a pure resolution fold

### Rationale

Personas layer like config precedence: bundled defaults, a personal library in a
user-owned directory, and repo-committed personas. This is the natural answer to "some
committed, some in a home dir," and it ties cleanly to the executor model — CI runs from
the committed config, so repo-scoped personas are the only ones CI can use, while library
personas serve the app/one-shot executors. Resolution (the merged, de-duplicated,
precedence-ordered set) is a **pure fold** over the three sources; only the reading of
files is a shell concern (ADR-0001).

### Consequence

A persona carries an optional `scope` marker so a shell can badge its source and decide
what CI may use. "Promote to repo" is the ADR-0002 PR-shaped write; "copy to library" is a
free local copy.

---

## Decision 2 — Explicit import, never home-directory auto-discovery

### Rationale

There is no standard on-disk format for "an agent persona": Claude Code uses
`.claude/agents/*.md` with YAML frontmatter, others use different YAML/JSON/MD shapes, and
OpenAI's live server-side. Scanning a home directory for these means guessing formats,
tolerating false positives, and reading through a user's private files — and it produces a
pile of half-mapped personas nobody trusts. The only genuinely portable thing across all
sources is the **system prompt plus a name**; model, provider, and tier are
Peanut-Gallery-specific decisions the user must make regardless. So every import is really
"seed the prompt and name, then fill the Peanut Gallery bits" — a bounded mapping best done
as an explicit, reviewed action.

### Consequence

Import is a deliberate action that pre-fills the persona editor for confirmation. The tool
never scans `$HOME` for third-party agent folders. A bounded, opt-in exception is offered:
detecting `.claude/agents/` in the *currently selected repo* (one repo, one known format).

---

## Decision 3 — First-class support for the Claude Code subagent format

### Rationale

Among the external formats, the Claude Code subagent (`.claude/agents/<name>.md`) is
structured, documented, and widely used, and maps almost 1:1 to a persona: frontmatter
`name` → `name`, the markdown body → `systemPrompt`, presence of `tools` → agent tier,
`model` → a provider/model pick. It is worth a dedicated importer; plain markdown/text is
the generic fallback (content → `systemPrompt`).

### Consequence

The importer has two mappers — a Claude-subagent parser and a generic markdown mapper —
both feeding the same confirm-in-editor flow. Other formats (OpenAI custom GPT/Assistants)
can be added later behind the same flow without changing the model.
