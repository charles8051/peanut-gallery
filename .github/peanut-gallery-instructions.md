# Review conventions

House rules for anyone reviewing this repository, human or model. `CONTRIBUTING.md`
is the fuller version written for contributors; this is the subset a reviewer needs.

## The one design rule: functional core, imperative shell

`PeanutGallery.Core` is a **pure core** — immutable values and total functions. Same
input, same output. No IO, no clock, no `Task`, no mutable state carried across calls.
A clock, socket, `IChatClient`, or file handle appearing in `Core` is a defect worth
filing; it belongs in a shell.

The shells are `PeanutGallery.Cli`, `PeanutGallery.Engine` and `PeanutGallery.Desktop`.
Config IO, the model client, PR-comment posting, fan-out concurrency and the agentic
tool loop are shell concerns. `IReviewer` is async and IO-bearing, so it lives in
`Engine`, not in `Core` — that placement is deliberate, not an oversight.

New work in a shell must consume the existing core fold rather than re-implement it.
A shell growing its own copy of planning, prompt assembly or rendering is worth
flagging.

### Two things that look like violations and are not

- **`using System.IO` in `Core`.** `Path.GetRelativePath`, `Path.IsPathRooted` and the
  other `Path` members are string manipulation and touch no filesystem. `PathSafety.cs`
  uses them correctly. `File` and `Directory` are the ones that do not belong.
- **`IChatClient` named in a `Core` doc comment.** Describing what a shell will supply
  is not a dependency on it.

## AOT and reflection

`Core` sets `IsAotCompatible`. It must stay reflection-free so every shell, including
a Native-AOT desktop build, can publish it. Reflection, dynamic codegen, or unbounded
generic instantiation in `Core` is a defect even when it compiles.

## Testing

`Core` is exhaustively unit-tested with no API keys and no network, and new core logic
should arrive with tests. `review --dry-run` uses an offline stub and needs no keys.
Tests live in `tests/PeanutGallery.{Core,Engine,Desktop}.Tests`.

## Style

`.editorconfig` governs: tabs for C#, LF endings, `Nullable` enabled solution-wide.
Formatting is not worth a review comment — the config decides it.

## Proportion

Prefer the smallest change that solves the problem. Interfaces and abstraction are
welcome where they earn their place; machinery that dwarfs the problem it solves is
the thing to push back on. A finding should name a concrete failure — an input, a
state, an outcome — rather than a stylistic preference.
