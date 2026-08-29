# Security Policy

## Reporting a vulnerability

Please report security issues **privately** via GitHub's
[**Report a vulnerability**](https://github.com/charles8051/peanut-gallery/security/advisories/new)
(the repository's *Security* tab → *Advisories*). Do not open a public issue for a
suspected vulnerability.

Include what you'd need to reproduce it: affected version/commit, configuration, and the
observed vs. expected behavior. We'll acknowledge the report and work a fix on a private
advisory before any public disclosure.

## What Peanut Gallery is trusted with

Peanut Gallery runs as a CI action that reads a pull request's diff, calls third-party
model providers, and posts review comments. Its threat model centers on **untrusted PR
content** (a diff, or a comment, can be attacker-controlled) and **provider API keys**.
The safeguards below are enforced in the action itself, not just in a consumer's
workflow `if:` — so they hold even if a consumer misconfigures the trigger.

- **Fork PRs are refused by default.** A review only runs when the PR head is the base
  repository. Fork PRs don't receive repository secrets and shouldn't consume the key or
  runner. Override is explicit (`--allow-fork`) and off by default.
- **Comment triggers are author-trust gated, and the gate fails closed.** A review
  triggered by `issue_comment` **or `pull_request_review_comment`** is refused unless the
  comment is from a non-bot `OWNER` / `MEMBER` / `COLLABORATOR` (pure
  `GitHubEventGuard`). This prevents a drive-by comment from spending your keys or
  steering the reviewer. Both triggers are gated because both carry an
  attacker-controlled body and the same author fields; the guard decides on payload
  *shape*, so it does not care which of the two fired. If the payload is absent,
  unreadable, or carries no comment object, the run is **refused** — once the event name
  says a comment triggered it, an author that cannot be established is not a trusted
  one.
- **Panel state is only believed from an author who speaks for the repo.** Each
  persona's session and the pinned panel live in PR comments, and a comment is something
  any reader can write. Every read of that state — the session blob, the pin (which
  carries the personas' system prompts and model ids), the metrics ledger, and the marker
  that decides which comment to update — is filtered to a bot or an `OWNER` / `MEMBER` /
  `COLLABORATOR` (`CommentTrust`). The same filter gates which comments can steer the
  reviewers, so the trust rule holds on a push-triggered run and not only on the
  `issue_comment` trigger.
- **Agent-tier tools are read-only and sandboxed.** The optional agentic reviewer gets
  only `read_file` / `grep` / `glob`, each **scoped to the checkout root**, with
  **path-traversal refused**, output size caps, and **no write, no shell, and no
  network**. Containment is decided after resolving symlinks (`FileSystemSafety`), so a
  link committed in a PR cannot read or search outside the checkout. A prompt-injected
  diff cannot make a tool write files, run commands, or exfiltrate data.
- **No secrets in the image or config.** Provider keys are read from environment
  variables at run time; the config only ever stores the *name* of the env var a key
  lives in (`apiKeyEnv`), never the key. The published container image contains no keys.
- **Reviews are advisory.** Output is posted as issue comments, not as a merge-blocking
  review, so a compromised or wrong review cannot block or force a merge on its own.

## Provider keys

Keys are supplied to a review through workflow secrets (`openrouter-api-key`,
`fireworks-api-key`, or the generic `provider-keys` input) and reach the process as
environment variables. Keep them in GitHub Actions secrets; never commit them. If you
believe a key was exposed, rotate it at the provider and update the secret.

## Supported versions

Peanut Gallery is pre-1.0 and ships from `main` (the action's moving `:main` image).
Security fixes land on `main`; there is no separate maintenance branch yet.
