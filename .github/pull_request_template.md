## What

<!-- What does this change do? -->

## Why

<!-- The motivation / the problem it solves. Link any issue: Closes #NN -->

## Checklist

- [ ] `dotnet test PeanutGallery.slnx -c Release` passes
- [ ] New logic in `PeanutGallery.Core` is pure (no IO/clock/`Task`/mutable state) and unit-tested
- [ ] Docs updated if behavior or structure changed (`docs/INDEX.md` gets a row for a new doc)
