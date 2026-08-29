# Peanut Gallery Documentation Index

> **Read this first.** It maps every question to the document that answers it, and
> states the docs convention. Add a row whenever you add a doc.

## Docs convention (the rule)

Two tracks, chosen by one question: **does this decision belong to one feature, or
is it a horizontal property of the whole system?**

- **Feature / vertical slice (the default):** `docs/feature-specs/<slug>/` with
  `spec.md` (the *what* — a living doc) and an optional `adr.md` (the *how* —
  decisions, internally numbered `Decision 1..N`, append-only). Reusable
  service/component contracts take the same shape under `docs/service-specs/<slug>/`.
- **Cross-cutting decision (the exception):** a numbered `docs/adr/NNNN-<slug>.md` —
  one immutable, citable, status-lifecycled decision per file, for a horizontal
  choice no single feature owns and many cite. **Number it at merge, not at
  authoring** (draft under a slug) so parallel branches never collide.

Templates to copy: [`templates/feature-spec.md`](templates/feature-spec.md),
[`templates/service-spec.md`](templates/service-spec.md).

## Question → Document

| I want to know… | Read this |
|---|---|
| What is Peanut Gallery / how do I add it to a repo? | [`/README.md`](../README.md) |
| Why one pure core projected to many shells? | [`adr/0001-functional-core-multi-shell.md`](adr/0001-functional-core-multi-shell.md) |
| What's built and what's next (engine, server, desktop GUI)? | [`roadmap.md`](roadmap.md) |
| How does a config become a set of reviews? | `ReviewPlanner.Plan` in [`src/PeanutGallery.Core`](../src/PeanutGallery.Core/ReviewPlanner.cs) |
| How does a review actually call a model? | [`feature-specs/engine-reviewer/spec.md`](feature-specs/engine-reviewer/spec.md) |
| How does it run automatically on a PR? | [`feature-specs/github-pr-review/spec.md`](feature-specs/github-pr-review/spec.md) |
| How do reviewers stay stateful across pushes? | [`feature-specs/stateful-sessions/spec.md`](feature-specs/stateful-sessions/spec.md) |
| How are very large diffs handled? | [`feature-specs/large-diffs/spec.md`](feature-specs/large-diffs/spec.md) |
| Does a finding say whether the diff introduced the hazard or inherited it? | [`feature-specs/finding-scope/spec.md`](feature-specs/finding-scope/spec.md) |
| Does the `scope` field earn its place — can a reviewer tell a hazard it introduced from one it inherited? | [`feature-specs/finding-scope/ab-finding-scope.md`](feature-specs/finding-scope/ab-finding-scope.md) |
| How does a continued turn tell code its own PR introduced from established API? | [`feature-specs/pr-own-baseline/spec.md`](feature-specs/pr-own-baseline/spec.md) |
| Does telling a turn what its own branch introduced stop the false breaking-change finding — and does it hide real ones? | [`feature-specs/pr-own-baseline/ab-pr-own-baseline.md`](feature-specs/pr-own-baseline/ab-pr-own-baseline.md) |
| How do we know how the panel is performing over time (flake/refute/cost)? | [`feature-specs/run-metrics/spec.md`](feature-specs/run-metrics/spec.md) |
| How does a degraded (partial) review become visible at decision time / block a gate? | [`feature-specs/degraded-panel-visibility/spec.md`](feature-specs/degraded-panel-visibility/spec.md) |
| How do I wait for a PR's review to land, and know it is THIS push's? | [`feature-specs/await-review/spec.md`](feature-specs/await-review/spec.md) |
| How long may a review run, and how is the reasoning-runaway flake bounded? | [`feature-specs/review-budget/spec.md`](feature-specs/review-budget/spec.md) |
| Do reviewers respond to author comments? | [`feature-specs/conversational-reviewer/spec.md`](feature-specs/conversational-reviewer/spec.md) |
| What does a comment cost, and how do I make it cost less? | [`feature-specs/conversation-modes/spec.md`](feature-specs/conversation-modes/spec.md) |
| How do I skip review on a PR? | [`feature-specs/opt-out/spec.md`](feature-specs/opt-out/spec.md) |
| What will the desktop GUI look like / how is it laid out? | [`feature-specs/desktop-gui/spec.md`](feature-specs/desktop-gui/spec.md) (+ `mockups/`) |
| Where do reviews run, and how does a shell touch a repo's workflow files? | [`adr/0002-review-executors-and-workflow-file-boundary.md`](adr/0002-review-executors-and-workflow-file-boundary.md) |
| Which prompt channel may untrusted text use, and why is a delimiter not a boundary? | [`adr/0003-prompt-channel-trust-boundary.md`](adr/0003-prompt-channel-trust-boundary.md) |
| How are personas discovered, scoped, and imported? | [`feature-specs/persona-management/spec.md`](feature-specs/persona-management/spec.md) |
| How would an orchestrator build a per-PR adversarial panel ("auto" mode)? | [`feature-specs/auto-panel/spec.md`](feature-specs/auto-panel/spec.md) |
| Is an auto-convened panel actually better than the fixed one? | [`feature-specs/auto-panel/ab-evaluation.md`](feature-specs/auto-panel/ab-evaluation.md) |
| Can a self-hosted GPU replace the paid finder models? | [`feature-specs/auto-panel/ab-local-inference.md`](feature-specs/auto-panel/ab-local-inference.md) |
| Was the `yagni` lens tried, and why was it reverted? | [`feature-specs/auto-panel/ab-yagni-lens.md`](feature-specs/auto-panel/ab-yagni-lens.md) |
| Does the `disproportion` lens catch machinery that dwarfs its problem? | [`feature-specs/auto-panel/ab-disproportion-lens.md`](feature-specs/auto-panel/ab-disproportion-lens.md) |
| Can the panel itself cause over-engineering, and what stops it? | [`feature-specs/auto-panel/ab-proportionality-clause.md`](feature-specs/auto-panel/ab-proportionality-clause.md) |
| How would a whole-repo review produce an artifact / its own PR? | [`feature-specs/repo-report/spec.md`](feature-specs/repo-report/spec.md) |
| What does a config look like? | [`/examples/peanut.json`](../examples/peanut.json) |
| Conventions, build, workflow, docs layout | [`/CONTRIBUTING.md`](../CONTRIBUTING.md) |
| House rules fed to this repo's own reviewers | [`/.github/peanut-gallery-instructions.md`](../.github/peanut-gallery-instructions.md) |
| Tracked future work, bugs, risks | [`/BACKLOG.md`](../BACKLOG.md) |
| All cross-cutting decisions | [`adr/README.md`](adr/README.md) |
