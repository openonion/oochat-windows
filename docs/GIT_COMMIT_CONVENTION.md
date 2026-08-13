
# Git Commit Convention

This project uses Conventional Commits.

## Format

```text
<type>(<scope>): <summary>
```

Examples:

```text
feat(chat): add streaming status display
fix(storage): handle missing SQLite database directory
docs(readme): update desktop setup instructions
test(agent): add connection validation coverage
```

## Types

- `feat`: new user-facing or developer-facing functionality
- `fix`: bug fix
- `docs`: documentation changes
- `test`: tests or testing utilities
- `refactor`: code restructuring without behavior changes
- `style`: formatting-only changes
- `chore`: dependency or maintenance work
- `build`: packaging or build configuration
- `ci`: CI workflow changes

## Recommended Scopes

- `chat`: chat UI, streaming events, and message rendering
- `agent`: agent configuration and connection management
- `sessions`: local conversation/session history
- `storage`: SQLite schema, repositories, migrations
- `protocol`: `ConnectOnion.Protocol` wire format, signing, WebSocket state machine
- `runtime`: app-level run/turn runtime (`AgentSessionManager`, projection, connection registry)
- `settings`: desktop preferences
- `usage`: token-usage ledger and the Usage panel
- `files`: drag-and-drop files and attachments
- `notifications`: Windows/in-app toasts and activation routing
- `ui`: shared UI components, styles, and layout
- `docs`: project documentation
- `ci`: GitHub Actions and validation workflows
- `build`: Windows/MSIX packaging and release output

## Good Examples

```text
feat(agent): save agent configuration in SQLite
feat(sessions): add local conversation list
feat(files): add attachment drop zone
fix(chat): show failed connection error
refactor(runtime): split turn projection out of the session manager
perf(protocol): prevent large JSON frame buffers from leaking across turns
test(storage): cover schema migration path
ci(github-actions): run headless unit tests and the conformance gate
```

## Branch Names

```text
feature/agent-config
feature/session-history
bugfix/connection-error
refactor/sqlite-repositories
perf/navigation-reuse-and-buffer-leaks
```

## Pull Requests

Use the same convention for PR titles:

```text
feat(chat): display agent execution status
```

PR descriptions should include:

- Summary
- Related issue
- Main changes
- Testing evidence
- Known limitations

## Rules

- Keep commits small and focused.
- Reference the related GitHub issue when available.
- Do not mix unrelated modules in one commit.
- Run relevant validation before opening a PR.
