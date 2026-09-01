# Peanut Gallery

**A panel of opinionated code reviewers on your pull requests. Bring your own models.**

Most review bots give you one reviewer with one opinion. Peanut Gallery convenes a
*panel* — an architect, a bug-hunter, a contrarian who argues the change should not
exist — each running on a model you choose, speaking through a verdict that updates
itself as you push.

## Quickstart

Drop it into any repository's PR workflow:

```yaml
# .github/workflows/review.yml
on:
  pull_request:
    types: [opened, reopened, synchronize, ready_for_review]
permissions:
  contents: read
  pull-requests: write
jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
      - uses: charles8051/peanut-gallery@aa1f3845086e0400b5d64070b876a45444207bad   # v0.1.1
        with:
          openrouter-api-key: ${{ secrets.OPENROUTER_API_KEY }}
```

That is a working review on every push to a PR. With no config committed you get the
default panel described below. Keep the checkout: it is what lets the reviewers read
around the diff rather than seeing the patch alone.

> Pinned to a commit, with the release tag in a comment. This action runs with
> `pull-requests: write` and your model key, so a moving reference is a moving trust
> boundary — and a tag can be force-moved over a published release, which a commit cannot.
> That is what GitHub's hardening guidance recommends for third-party actions. Read the
> tag comment to see which version you are on; bump both together.

### Opt-ins

Neither of these is needed for a working review.

**Talk back to the panel.** Add the comment trigger, and a reply explaining that a
finding is intentional gets it withdrawn:

```yaml
on:
  issue_comment:
    types: [created]        # an EDIT to an existing comment does not re-run — see below
```

**Skip no-op jobs.** With the comment trigger on, every comment anywhere in the
repository starts a job. The action exits out of the ones that are not a human comment
on a PR, but only after a runner has taken the job and pulled the container. This filter
decides it while the cost is still unpaid:

```yaml
jobs:
  review:
    if: >-
      github.event.comment.user.type != 'Bot' &&
      (github.event_name != 'issue_comment' || github.event.issue.pull_request)
```

Cost, not correctness: since v0.1.1 the action refuses a bot comment and skips a comment
on a plain issue on its own. Worth adding on a busy repository.

## The default panel

Two reviewers always run: an **architect** and a **bug-hunter**. An orchestrator reads
the diff when the PR opens and convenes up to two more aimed at what this particular
change risks — then pins them for the life of the PR, so the panel does not churn
between pushes.

They speak with one deduplicated comment rather than four, and they answer comments
addressed to `@peanut-gallery` with a single reconciliation call rather than a full
panel turn.

The two seeded reviewers run whether or not the orchestrator succeeds, so a planner
failure leaves you with a review rather than an empty board.

Commit a [`peanut.json`](examples/peanut.json) when you want to pin your own lenses.

## Reviews are stateful

Every persona keeps its own session — last SHA, running summary, open findings — and
that state rides inside the comment it is rendered in: the panel's single comment under
the default `comment: panel`, or the persona's own comment under `perPersona`. A new
push sends only the delta, and the reviewer reports what changed and what got resolved
rather than starting over.

**A review runs on a push to the PR, and on a new PR comment** — the latter needs the
`issue_comment` opt-in above. A comment is how you talk back: explain
that a finding is intentional and the reviewer withdraws it. Editing an existing comment
does *not* trigger a review — `types: [created]` is deliberate, since fixing a typo in a
reply is not worth a full panel turn. Post a new comment instead.

What a comment costs is configurable via
[`conversation`](docs/feature-specs/conversation-modes/spec.md): a `mentions` gate so
only comments addressed to the panel count, and a mode of `reconcile` (one call decides
what comes off the board), `panel` (every persona takes a full turn), or `off`. The
bundled default uses `reconcile` with `@peanut-gallery`, so two humans talking in a PR
thread cost nothing.

## Inputs

| Input | |
|---|---|
| `openrouter-api-key` / `fireworks-api-key` | convenience inputs for the two common providers |
| `provider-keys` | any other provider — one `KEY=VALUE` per line, exported before the review runs |
| `config` | repo-relative path to a config; omit for the default panel |
| `pr-number` | defaults to the triggering PR |
| `github-token` | defaults to the workflow token |

A config's provider block names only the *environment variable* its key lives in, never
the key itself. So any OpenAI-compatible provider works through `provider-keys`:

```yaml
      - uses: charles8051/peanut-gallery@aa1f3845086e0400b5d64070b876a45444207bad   # v0.1.1
        with:
          config: .github/peanut-gallery.json
          provider-keys: |
            NVIDIA_API_KEY=${{ secrets.NVIDIA_API_KEY }}
            TOGETHER_API_KEY=${{ secrets.TOGETHER_API_KEY }}
```

> **Runner note:** the action runs a prebuilt image from GHCR rather than building a
> Dockerfile per job, so `ubuntu-latest` needs no setup. Cross-repo `uses:` also needs
> Settings → Actions → "Allow access from other repositories".

## Documentation

- [`docs/INDEX.md`](docs/INDEX.md) — the question → document map, and the place to start
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — conventions, build and test, how to open a PR
- [`SECURITY.md`](SECURITY.md) — threat model and how to report a vulnerability privately

## License

[MIT](LICENSE). Copyright (c) 2026 Charles Lee.
