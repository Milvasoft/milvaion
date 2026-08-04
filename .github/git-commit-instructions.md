# Commit message instructions (Conventional Commits)

Write every commit message following the [Conventional Commits](https://www.conventionalcommits.org/) specification.

## Format

```
<type>(<scope>): <description>

[optional body]

[optional footer(s)]
```

- The **subject** line is `<type>(<scope>): <description>`.
- Keep the subject in the **imperative mood** ("add", "fix", "remove" — not "added"/"fixes"), lower-case, no trailing period, and **<= 72 characters**.
- `scope` is optional but preferred; use lower-case.
- Leave a blank line before the body and before the footer.
- Wrap the body at ~100 characters. Explain **what** and **why**, not how.

## Allowed types

- `feat` — a new feature
- `fix` — a bug fix
- `perf` — a performance improvement
- `refactor` — a code change that neither fixes a bug nor adds a feature
- `docs` — documentation only
- `style` — formatting/whitespace, no code behaviour change
- `test` — adding or fixing tests
- `build` — build system, dependencies, packaging
- `ci` — CI/CD configuration and scripts
- `chore` — housekeeping that doesn't touch src or tests
- `revert` — reverts a previous commit

## Suggested scopes (this repo)

Pick the most specific scope that fits: `api`, `ui`, `infrastructure`, `application`, `domain`, `sdk`, `mcp`, `settings`, `jobs`, `workflows`, `occurrences`, `alerting`, `redis`, `auth`, `db`, `deps`, `ci`, `docs`. Omit the scope if a change spans many areas.

## Breaking changes

- Add a `!` after the type/scope **and** a `BREAKING CHANGE:` footer:

```
feat(api)!: change occurrence list response shape

BREAKING CHANGE: `totalCount` is now an estimate, not an exact count.
```

## Rules

- One logical change per commit; the subject should describe the whole change.
- Do not invent a scope that isn't reflected by the changed files.
- Do not include file lists in the message; summarise the intent.

## Examples

```
feat(settings): add runtime branding and notification config
fix(redis): estimate occurrence counts from planner stats to avoid full scans
perf(ui): format execution total with compact K/M notation
refactor(infrastructure): qualify SettingsProvider registration to avoid BCL type clash
docs(mcp): document settings tools
build(deps): bump Npgsql to 10.0.10
test(alerting): stub ISettingsProvider in AlertNotifier tests
chore(ci): silence MCP002 analyzer suggestion
```
