# GithubDash

A lightweight, animated desktop radar for GitHub pull requests — Windows and macOS, C# / .NET 10, Avalonia 11.

## Features

| Area | What it does |
|---|---|
| Accounts | Any number of GitHub accounts. Sign in with the browser (OAuth device flow) or with a personal access token. |
| Repositories | Pick exactly which repositories to monitor, from your own and from your organizations. Nothing else is synced. |
| Dashboard | Animated cards: open pull requests, assigned to me, needs my approval. Per-repository bars and a recent-activity feed. |
| People | Each repository is a floor; each person is an animated figure with their GitHub photo, showing pull requests raised and waiting on them. Click a person for their pull requests. Per-floor statistics. |
| My PRs | Cover-flow cards with filters (Assigned to me, All PRs, Requested review, Own PRs). Click the front card for a popup with tabs: day-grouped timeline with filters, description, comments (by me / by others / mentioning me) and approvals. |
| Live updates | Each repository is checked for changes every few seconds (default 10 s) and only downloaded when something changed. |
| Notifications | Choose which events notify you (assigned, added as reviewer, new comments on your pull requests, merged). Animated in-app toasts, or switch to OS notifications in Settings. |
| Widgets | A draggable always-on-top floating widget, and a native tray icon (Windows) / menu-bar item (macOS) with the same three numbers. |
| Timers | Task timer, Pomodoro timer and pull request review timer, with session history. |

## Build and run

Requires the .NET 10 SDK.

```
dotnet build
dotnet run --project src/GithubDash.App
dotnet run --project src/GithubDash.App -- --demo     # offline sample data, nothing is saved
dotnet test
```

Publish a self-contained build:

```
dotnet publish src/GithubDash.App -c Release -r win-x64   --self-contained
dotnet publish src/GithubDash.App -c Release -r osx-arm64 --self-contained
```

## Signing in

**Browser sign-in (device flow).** Register an OAuth App once at GitHub → Settings → Developer settings → OAuth Apps,
tick *Enable Device Flow*, and paste its **client ID** into Settings. No client secret is used or stored.
Requested scopes: `repo` (read pull requests in private repositories) and `read:org` (list organization repositories).
Organizations with OAuth app restrictions must approve the app before their repositories appear.

**Access token.** A fine-grained token with read-only *Pull requests* and *Metadata* permissions is enough
(classic tokens: `repo`, `read:org`). For organizations that enforce SAML SSO, authorize the token for the organization.

## Architecture

Clean architecture; dependencies point inwards only.

```
src/
  GithubDash.Domain          Entities and pure rules: PullRequest, change detection, people insights, timers. No dependencies.
  GithubDash.Application     Use cases and ports: PullRequestMonitor, AccountService, SettingsService, NotificationRouter.
  GithubDash.Infrastructure  Adapters: GitHub GraphQL/REST gateway, device flow, secret stores, JSON stores, OS notifications, demo data.
  GithubDash.Presentation    View-models (framework-free MVVM, unit-testable without a UI).
  GithubDash.App             Avalonia views, windows, custom controls and the composition root (Services/AppHost.cs).
tests/
  GithubDash.Tests           xUnit tests for domain rules, JSON mapping, security helpers and view-models.
```

Only `GithubDash.App` (Avalonia) and the test project use NuGet packages.

## Security

- **Tokens never touch the settings file.** Windows: DPAPI, bound to the signed-in Windows user. macOS: login Keychain.
  (Linux development fallback: AES-GCM with a user-only key file — weaker, documented in code, never used on Windows/macOS.)
- **Tokens only go to GitHub.** One `HttpClient` with a host allow-list (`api.github.com`, `github.com`), HTTPS only, redirects disabled. The avatar host is allowed only for requests that carry no credentials.
- **Read-only.** The app never writes to GitHub. Device flow needs no client secret.
- **Untrusted input is treated as such.** Links are opened only if they are `https://…github.com`; notification text is passed to the OS
  through environment variables / argv, never spliced into a command; pull request text is rendered as plain text; the settings file is validated on load.
- **No token in errors or logs.** Error messages are built from status codes and GitHub's message only.
- Single instance per user, no elevation (`asInvoker`), atomic file writes, user-only file permissions on Unix.

## Footprint

- Live checks are conditional requests: an unchanged repository answers "304 Not Modified", which GitHub does not count against the API quota. A GraphQL download happens only for a repository that changed (plus a safety resync every 5 minutes).
- Profile pictures are downloaded once at thumbnail size, without credentials, from avatars.githubusercontent.com only.
- Only the visible page is updated; hidden pages do no work.
- Looping animations run only while the main window is visible, and stop entirely when it is hidden, minimised or *Reduce motion* is on.
- Timers tick once per second only while a timer is running; number roll-ups create a timer for ~0.4 s and stop.
- Workstation, non-concurrent GC.

## Known limits

- "Live" means near-real-time checking, not push: GitHub can only push events to a webhook on a public server, which a desktop app does not have.
- The description and comments are shown as plain text; Markdown is not rendered.
- The detail popup loads the latest 150 timeline items and up to 50 inline comments per review.

- Review requests addressed to a **team** are not counted as "needs my approval" (only direct requests are).
- Per repository, the 50 most recently updated open pull requests are tracked; comment counts "by me" look at the latest 100 comments/reviews.
- OS notifications on Windows are shown under the *Windows PowerShell* app identity, because an unpackaged app has no identity of its own.
- A true Windows 11 Widgets-board widget or macOS WidgetKit widget needs a packaged (MSIX / Swift) extension and is not included;
  the tray / menu-bar counter is the native option.

## Round 3, batch 1 (what's new)

- **Light mode** (Settings → General → Theme: Dark / Light / System).
- **Home page** with your GitHub profile picture, bio, stats and organizations, plus today's desk numbers.
- **Dashboard desk**: an animated desk with one "book" per PR waiting on you (assigned books are outlined, older PRs are thicker).
- **Floating widgets**: semi-transparent and click-through (they light up under the mouse but never steal a click). The PR widget also shows the oldest assigned PR's wait in seconds / minutes / hours / days. A second widget shows the running timer. To move them, switch on *Settings → Move the floating widgets*, drag, then switch it off; the position is remembered.
- **Notifications**: *Close notifications automatically* can be switched off, so toasts stay until you close them.
- **My PRs**: PRs assigned to you have a glowing frame and an "ASSIGNED TO YOU" ribbon; descriptions and comments render Markdown; the Comments tab has an **AI comments** filter (Copilot, CodeRabbit, Claude, …).
- **People**: each card now shows raised, assigned, waiting and the oldest wait.
- **AI tool limits** (Settings): add Claude, ChatGPT and others, press *Limit reached* to start the countdown; an alarm and a notification fire when the tool is usable again. (No vendor offers a read-only usage API for subscriptions, so the limit start is marked by hand.)
- **Accounts** show avatar, name, sign-in method and watched-repository count; the repository picker is collapsible.

## Batch 2

- **People office**: avatars walk in and sit at desks; click a person for a full activity dashboard (PRs raised/open/merged/closed, comments, reviews, pending branches, recent PRs and events).
- **SQLite storage** (`githubdash.db`, next to settings): PR snapshots, person activity and timer sessions. On start the saved data shows instantly and a normal sync then updates it (ETag-based). Settings stay as editable JSON. Needs a NuGet restore for `Microsoft.Data.Sqlite`.
- **Dynamic island** (Settings > Floating widgets): a pill at top centre with your counts and timer. It grows to show notifications (replacing corner toasts while on) and on hover shows the oldest waiting PR. "Nusa" was not specified, so this is my interpretation of a notch-style island.

## Refinements (round 4)

- **Floating widget** is a small circle with the assigned count and a red bubble for unseen notifications; hover expands it to the card (counters plus the oldest assigned wait only).
- **Dynamic island** can be moved (Settings > Floating widgets > "Move the floating widgets and the island"), is click-through while locked and expands when the cursor is over it.
- **Notifications:** an option to also show the toast when the island is on, and a switch per notification type for "disappears by itself" or "stays until closed".
- **Taskbar / Alt+Tab:** widgets, island and toasts are tool windows and no longer appear there.
- **Dashboard desk:** a bookcase on the desk; every PR is listed in full beside it in a scrolling list.
- **Home:** "Assigned to me" is the first block, oldest first.
- **People:** entrance animation plays once per person per session; roomier room with a perspective floor; `W` (waiting) and `A` (assigned) comic bubbles.
- **PR cards:** STALE APPROVAL, UPDATED SINCE YOUR REVIEW, STALE REVIEW and RE-APPROVAL badges. A review is stale when its time is before the newest commit's committed date.

## Refinements (round 5)

- **Overlays:** opacity sliders for the widget and the island (Settings), and a "click through" option: mouse clicks pass to the window underneath. A notification that does not auto-close keeps the island clickable so it can be dismissed. The widget card now grows out of the circle and shrinks back with an animation.
- **Two waits everywhere:** Oldest Assigned (how long a PR has been assigned to you) and Oldest Review (how long your approval has been awaited) on the widget, island, Home, Dashboard desk, People floors and each person.
- **People:** collapsible sections in the person panel, status line "Assigned · PR · Waiting" with the oldest assigned and review waits on their own lines, numbers in coloured circles on each desk, new entrance (walks in from the back, scales up, turns around using a 3D rotation, once per person).
- **Notifications:** "Removed as assignee" (switch and its own stay/disappear switch).
- **Repository filter:** one drop-down above Home, Dashboard, People and My PRs: all repositories or one.
- **Home:** profile on top, "Assigned to me" below with a fixed border.

## Round 6 changes

- **Widget flicker / sideways drift fixed.** The widget now positions itself absolutely (circle place from saved percentages, card place derived from it, kept on screen) and hover is tested against the size and place we set, not the laid-out bounds that lag a resize. The circle no longer moves between mini and expanded, and the mini circle has no shadow.
- **Position options** (Settings > Floating widgets): the full widget's place on screen (0-100 % horizontal/vertical of the free room, always on screen) and, separately, where the mini circle sits *inside* the full widget's area (0-100 %). On hover the card grows from the circle's spot to the full widget (scale animation about the circle's centre); on leave it shrinks back into it. Dragging while unlocked moves the full widget and is saved as percentages of the main screen.
- **Mini widget switch:** on (default) = small circle that expands on hover; off = the full card always.
- **Tray / menu-bar menu:** Show floating widget, Mini widget, Show dynamic island, Move widgets and island (check items kept in sync with Settings).
- **Taskbar / Dock badge:** the assigned-to-me count as an overlay badge on the Windows taskbar button and the macOS Dock tile (hidden when 0; re-applied when the window is shown).
