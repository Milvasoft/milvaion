# Copilot Instructions

## General Guidelines
- First general instruction
- Second general instruction

## Code Style
- Use specific formatting rules
- Follow naming conventions

## Testing Practices
- Integration tests should prefer obtaining services like IMilvaLogger directly from the ServiceProvider instead of using stubs or mocks.

## Commit Messages
Always generate commit messages that follow the [Conventional Commits](https://www.conventionalcommits.org/) specification.

- Format: `<type>(<scope>): <description>`, optionally followed by a blank line, body, and footer(s).
- Subject: imperative mood ("add", "fix", "remove"), lower-case, no trailing period, at most 72 characters.
- Types: `feat`, `fix`, `perf`, `refactor`, `docs`, `style`, `test`, `build`, `ci`, `chore`, `revert`.
- Scope (optional, lower-case) is the affected area, e.g. `api`, `ui`, `infrastructure`, `application`, `domain`, `sdk`, `mcp`, `settings`, `jobs`, `workflows`, `alerting`, `redis`, `auth`, `db`, `deps`. Omit it when a change spans many areas; never invent a scope not reflected by the changed files.
- Breaking changes: add `!` after the type/scope and a `BREAKING CHANGE:` footer.
- Describe intent (what/why), not a file list. Reference issues in the footer when relevant (`Closes #123`).
- Full rules and examples: [.github/git-commit-instructions.md](git-commit-instructions.md).