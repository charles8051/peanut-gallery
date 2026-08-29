# Backlog

Work for this repo is tracked as **GitHub Issues**, not in this file.

- Browse: <https://github.com/charles8051/peanut-gallery/issues>
- Open work, by priority + kind:
  `gh issue list -R charles8051/peanut-gallery --state open`
- Add an item (enqueue — don't append here):
  `gh issue create -R charles8051/peanut-gallery -t "..." -l kind:todo -l p2 -b "..."`

Classify with a `kind:` label (`kind:todo` / `kind:risk` / `kind:bug`) and a priority
`p1`–`p3` (P0 = do now → P3 = later/conditional). Log
bugs and risks proactively, without being asked.

---

The original `BACKLOG.md` items were drained to issues on 2026-06-25
([#2](https://github.com/charles8051/peanut-gallery/issues/2)–[#8](https://github.com/charles8051/peanut-gallery/issues/8)).
Two items were already complete at that point and so were not filed: live-verifying
the Engine against real models (OpenRouter), and deploying the PR autoreview on a
self-hosted runner.
