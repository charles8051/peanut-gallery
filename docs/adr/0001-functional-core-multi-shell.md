# ADR 0001: One pure core, projected to multiple shells

## Status

Accepted. The founding architectural decision for Peanut Gallery; every shell
(CLI, headless server, desktop GUI) and the engine are built against it.

## Context

Peanut Gallery must run the *same* review logic from very different places: a CLI
kicked off at a workstation, a headless service on a VM with a web management page,
and a dead-simple local desktop GUI where you drag personas onto repos. An earlier
tool by the same author proved the shape we want — a pure, reflection-free core
shared verbatim by both a CLI and an instant-spawn, Native-AOT Avalonia board, so
the two surfaces could never disagree about the domain. The prime directive here — functional core, imperative shell — says
the same thing.

The risk we are designing against is the usual one: business logic leaking into UI
or transport code, so each surface re-implements "what a review is" slightly
differently and they drift.

## Decision 1 — All review logic is a pure core; surfaces are thin shells

`PeanutGallery.Core` contains only immutable values and total functions: personas,
providers, the diff model, `ReviewPlanner.Plan` (the central fold from config + a
diff to the exact set of review tasks), `PromptAssembly`, `CommentRenderer`,
`ConfigValidation`. Same input → same output, no IO, no clock, no `Task`, no mutable
state carried across calls.

**Rationale.** The core is exhaustively unit-testable with no API keys and no
network; whole classes of bugs (surfaces disagreeing about the plan) become
unrepresentable because every shell runs the identical fold.

**Consequence.** A clock, socket, `IChatClient`, or file handle appearing in the
core is a defect to be moved to a shell, not an exception to the rule.

## Decision 2 — IO, the model client, and concurrency live only in shells

Config IO, the provider/model client, PR-comment posting, fan-out concurrency, and
the agentic tool loop are shell concerns. The `IReviewer` port — async and
IO-bearing — is defined in the shell, deliberately not in the core, even though it
returns a core `PersonaReview`.

**Rationale.** Keeps the "no `Task` in the core" line bright and the core trivially
portable across the CLI, server, and GUI.

**Alternatives rejected.** Putting an async `IReviewer` (or an `IChatClient`
dependency) into the core would couple the foundation to a specific SDK and a
threading model, defeating the point.

## Decision 3 — The core is provider-agnostic and serialization-free

The core models chat as its own `Message` / `ChatRole` / `ReviewRequest` values and
carries no JSON attributes. Shells map those onto whatever they use — a shell maps
`ReviewRequest` onto a Microsoft.Extensions.AI `IChatClient` call; a shell owns
reading/writing `peanut.json`.

**Rationale.** The foundation stays dependency-light and reflection-free, which is
also what makes it `IsAotCompatible` — a prerequisite for the Native-AOT Avalonia
desktop shell.

**Consequence.** Config DTOs and a source-generated `JsonSerializerContext`, when
needed for AOT, live in the shell; the core never learns about serialization.

## Decision 4 — The core stays AOT-clean

`PeanutGallery.Core` sets `IsAotCompatible=true` so the trim/AOT analyzers run at
the source. Any addition that would break a Native-AOT shell is flagged here, not
discovered at publish time.

**Rationale.** The desktop GUI's whole value proposition (instant spawn, no Electron)
depends on AOT; guarding the core keeps that path open for free.
