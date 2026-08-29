# Feature Spec: [Feature Name]

## Status
<!-- Draft | In Review | Approved | In Progress | Implemented | Deprecated -->
**Draft**

| Field        | Value        |
|--------------|--------------|
| Author       | [your name]  |
| Created      | [YYYY-MM-DD]  |
| Last Updated | [YYYY-MM-DD]  |

## Purpose
<!-- 1-3 sentences. What problem does this feature solve? What is the outcome? -->

## Affected layers

| Project / area              | Change type |
|-----------------------------|-------------|
| `PeanutGallery.Core`        | <!-- new value / fold / NONE (the core stays pure) --> |
| `PeanutGallery.Cli`         | <!-- new verb / option / none --> |
| `PeanutGallery.Engine`      | <!-- model client / reviewer / none --> |
| Server shell (roadmap)      | <!-- endpoint / page / none --> |
| Desktop shell (roadmap)     | <!-- view / gesture / none --> |

> Reminder (ADR-0001): new logic belongs in the pure core; shells consume the fold,
> they do not re-implement it. IO / clock / `Task` / model client stay in a shell.

## Requirements
<!-- Checkboxes become acceptance criteria. -->
- [ ] [Requirement 1]
- [ ] [Requirement 2]

## Core changes
<!-- New immutable values or total functions. Confirm they remain IO-free and AOT-clean. -->

## Shell changes
<!-- The imperative edges: IO, the model client, concurrency, posting comments. -->

## Config / contract changes
<!-- New PeanutConfig fields, persona/provider shape, JSON shape. -->

## Out of scope
<!-- What this feature deliberately does not do. -->

## Open questions
- [ ] [Question] — *Owner: [name]*

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Founding ADR | [`docs/adr/0001-functional-core-multi-shell.md`](../../adr/0001-functional-core-multi-shell.md) |
| Sibling ADR (the how) | [`adr.md`](adr.md) |
