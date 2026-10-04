# Workspace chat: sidebar session context menu: implementation summary and proof

Commit: `f51a70a9` on `main`, pushed to fork `larroy/openclaw-windows-node`.

## Scope delivered

Right-click menu on Workspace chat sidebar session rows, mirroring the web chat session menu plus Windows extras, and a collapsible "Archived" section at the sidebar bottom.

Core parity: Pin/Unpin, Rename, Mark as unread/read, Archive, Fork conversation, Copy > (session key, session ID, as Markdown), Delete.
Windows extras: Reset, Compact, Export transcript (same behavior as the Sessions page).
Archived section rows offer: Unarchive, Fork, Copy, Delete.
State rendering: pinned rows first with a pin glyph, unread accent dot, archived rows hidden from the active list.

Out of scope (unchanged, per plan): Icon & color, Move to group, Assign to, Open in.

## Architecture

| Responsibility | Owner |
|---|---|
| Pure menu entries, patch payloads, fork request | `Presentation/WorkspaceSessionMenu.cs` |
| Flyout population, dialogs, gateway dispatch, read acknowledgement | `Windows/WorkspaceSessionMenuController.cs` |
| Archived session list snapshot | `Services/WorkspaceArchivedSessionsSource.cs` |
| Sidebar row application, archived toggle, navigation/leave | `Windows/WorkspaceWindow.xaml.cs` |
| Row ordering, archived/agent filtering, titles | `Presentation/WorkspaceProjection.cs` |
| Icon glyph constants | `Helpers/FluentIconCatalog.cs` |
| Ownership ledger | `docs/ARCHITECTURE.md` (new authoritative row; `WorkspaceWindow.xaml.cs` row extended) |

### Shared protocol (`src/OpenClaw.Shared`)

- `SessionInfo`: `Pinned`, `PinnedAt`, `Unread`, `MarkedUnreadAt`, `Archived`.
- `PopulateSessionFromObject` parses `pinned`, `pinnedAt`, `unread`, `markedUnreadAt`, `archived` with authoritative-list reset semantics (mirrors the `thinkingLevel` reset pattern; explicit `TryGetProperty` shape so the drift guard sees each field).
- `SessionPatch`: `Label`, `Pinned`, `Unread`, `Archived`, `ExpectedMarkedUnreadAt` (documented as a guard, excluded from `HasChanges`). `SessionListResult` added.
- `SessionCreateRequest`: `Fork`, `ForkFrom`; `BuildSessionCreateParameters` emits `fork`/`forkFrom` only when `Fork` is set.
- `ListArchivedSessionsAsync(timeoutMs = 15000)` sends `sessions.list` with `archived:true`; `ParseDetachedSessionRows` returns detached rows that never touch live `_sessions` tracking or raise `SessionsUpdated` (wizard-response early return already bypasses `ParseSessions`).
- `IOperatorGatewayClient`: default `ListArchivedSessionsAsync` returns `IsSupported = false` so fakes stay compiling.
- `SessionTranscriptFormatter.FormatMarkdown(history, title)`: mirrors the web `buildChatMarkdown` (`## User`/`## Assistant`/`## Tool`, ISO-8601 UTC timestamps, blank-message skip, `null` when empty).
- Drift-guard snapshot (`gateway-protocol-snapshot.json`): `sessions.list` request `archived` + response `pinned/pinnedAt/unread/markedUnreadAt/archived`; `sessions.create` `fork`/`forkFrom`; `sessions.patch` `label/pinned/unread/archived/expectedMarkedUnreadAt`; `label` removed from the upstream not-yet-implemented list.

### Tray.WinUI wiring

- `RefreshSidebar` reconciles rows in place: `SyncSessionItems` keeps one `NavigationViewItem` per session key (active and archived caches), refreshes a reused row through `ApplySessionItem` only when its `WorkspaceSession` changed, removes stale rows, and orders rows after `SessionsEmpty` / `ArchivedEmpty` via `WorkspaceItemsSync.Arrange`. The selected container is never destroyed during `SelectionChanged`. Archived header hides when disconnected; chevron tracks expansion; empty/unavailable placeholders handled.
- `CreateSessionItem(session, automationPrefix)` sets the stable AutomationId and delegates to `ApplySessionItem`: pin glyph column, unread accent dot or busy ring, item status (`Pinned`, `Working`, `Unread`; always reset so reused rows drop stale status), per-row context flyout.
- `OnStateChanged`: archived refresh on `Sessions` change when expanded; `Clear()` when disconnected; refresh on reconnect when expanded.
- `RenderDestination`: fires `AcknowledgeReadAsync` for unread sessions on navigation (list refresh does not re-trigger it, so "Mark as unread" on the open session persists until re-navigation).
- Session creation split into `CreateAndSelectSessionAsync` shared by `NewSessionAsync` and `ForkSessionAsync`; `LeaveSession(key)` selects the next non-key session or returns to Home; `ShowInfo(message, severity)` added, `ShowError` now sets `InfoBarSeverity.Error` explicitly.
- Strings: 35 keys added to all six locales (`en-us`, `fr-fr`, `nl-nl`, `pt-br`, `zh-cn`, `zh-tw`), identical English values (parity-test requirement), no em dashes.

## Plan deviation (contingency applied)

The plan's fallback for `MenuFlyout.Target` was needed: `Target` was not populated for `NavigationViewItem.ContextFlyout`, so the shared-flyout `Opening` handler would have hidden the menu. Implemented `WorkspaceSessionMenuController.CreateFlyout(session)`: per-row `MenuFlyout` with the row captured at creation; population code is shared via `Populate(flyout, session)`. No behavior difference otherwise.

## Validation (AGENTS.md required set)

| Command | Result |
|---|---|
| `./build.ps1` | All 5 projects succeeded (Shared, Cli, WinNodeCli, SetupEngine, WinUI) |
| `dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore` | 4230 passed, 0 failed, 35 skipped |
| `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore` | 3899 passed, 2 failed |

The 2 tray failures are `NativeSpeechStackRuntimeTests.TestHost_AppLocalVCRuntime_MeetsOnnxRuntimeFloor` and `TrayBuildOutput_NativeTtsStack_LoadsWithAppLocalVCRuntime`: pre-existing environmental gap (no Visual Studio "C++ Redistributable Update" component on this machine; `OPENCLAW0001` warning fires before every test run and the failure text names the exact fix). Unrelated to this change; they fail identically on the clean tree when the component is absent.

Focused new tests: 22 shared (patch payload allowlist, guard-only patch, label clear-null, fork params, field parse/reset, detached archived rows, FormatMarkdown) + 16 tray (menu gating, archived layout, disconnect gating, session-ID omission, rename/read-ack/fork builders, projection pin ordering, latest-session independence, archived projection).

## Real behavior proof (pool `windows-winui-interactive`)

Launched `.\run-app-local.ps1 -NoBuild -Isolated` connected to the developer's live gateway (`ws://127.0.0.1:18789`; isolated log confirmed `sessions.subscribe` and model resolution). Right-click on the "main" session row opened the context menu showing:

- "Last active 2h ago" disabled header
- Pin session, Rename…, Mark as unread: enabled
- Archive session: disabled (main-session protection)
- Fork conversation, Copy > (Copy session key / Copy session ID / Copy as Markdown), Reset session…, Compact session…, Export transcript…: enabled
- Delete…: disabled, red destructive styling

The sidebar rendered the new collapsible "Archived" header below the Sessions list.

Not verified / blocked: full interactive pass of pin/rename/unread/fork/archive/delete flows and disconnected gating was not individually captured via computer-use screenshots; the pure-model tests cover the gating and payload contracts, and the menu itself is proven live above. The web Control UI cross-check of pin state was not captured (would need the web UI running against the same gateway).
