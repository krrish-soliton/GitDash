# GitDash

A lightweight, animated desktop radar for GitHub pull requests — Windows and macOS, C# / .NET 10, Avalonia 11.

## Features

| Area | What it does |
|---|---|
| Accounts | Any number of GitHub accounts. Sign in with the browser (OAuth device flow) or with a personal access token. |
| Repositories | Pick exactly which repositories to monitor, from your own and from your organizations. Nothing else is synced. |
| Dashboard | Animated cards: open pull requests, assigned to me, needs my approval. Per-repository bars and a recent-activity feed. |
| People | Each repository is a floor; each person is an animated figure with their GitHub photo, showing pull requests raised and waiting on them. Click a person for their pull requests. Per-floor statistics. |
| Pull requests | Cover-flow cards with filters (Assigned to me, All PRs, Requested review, Own PRs) and a multi-select author filter. Click the front card for a popup with tabs: day-grouped timeline with filters, description, comments (by me / by others / mentioning me) and approvals. |
| Live updates | Each repository is checked for changes every few seconds (default 10 s) and only downloaded when something changed. |
| Notifications | Choose which events notify you (assigned, added as reviewer, new comments on your pull requests, merged). Animated in-app toasts, or switch to OS notifications in Settings. |
| Widgets | A draggable always-on-top floating widget, and a native tray icon (Windows) / menu-bar item (macOS) with the same three numbers. |
| Timers | Task timer, Pomodoro timer and pull request review timer, with session history. |

## Build and run

Requires the .NET 10 SDK.

```
dotnet build
dotnet run --project src/GitDash.App
dotnet run --project src/GitDash.App -- --demo     # offline sample data, nothing is saved
dotnet test
```

Publish a self-contained build:

```
dotnet publish src/GitDash.App -c Release -r win-x64   --self-contained
dotnet publish src/GitDash.App -c Release -r osx-arm64 --self-contained
```

## Installing

Download the installer for your system from the [Releases](../../releases) page.

- **Windows** (`GitDash-Setup-<version>.exe`): the installer asks whether to install **for all users** (needs administrator rights, installs under Program Files) or **only for me** (no administrator rights, installs under your own profile). The same choice can be made from a script: `GitDash-Setup-<version>.exe /CURRENTUSER` or `/ALLUSERS`, plus `/VERYSILENT` for an unattended install. A portable zip is also provided.
- **macOS** (`GitDash-<version>-osx-arm64.dmg` for Apple silicon, `-osx-x64.dmg` for Intel): open the disk image and drag GitDash to Applications. Drag it to `~/Applications` instead to install for your own account only. The app is not notarised, so open it the first time with right-click, then Open.

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
  GitDash.Domain          Entities and pure rules: PullRequest, change detection, people insights, timers. No dependencies.
  GitDash.Application     Use cases and ports: PullRequestMonitor, AccountService, SettingsService, NotificationRouter.
  GitDash.Infrastructure  Adapters: GitHub GraphQL/REST gateway, device flow, secret stores, JSON stores, OS notifications, demo data.
  GitDash.Presentation    View-models (framework-free MVVM, unit-testable without a UI).
  GitDash.App             Avalonia views, windows, custom controls and the composition root (Services/AppHost.cs).
tests/
  GitDash.Tests           xUnit tests for domain rules, JSON mapping, security helpers and view-models.
```

Only `GitDash.App` (Avalonia) and the test project use NuGet packages.

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
- **Pull requests** (formerly My PRs): PRs assigned to you have a glowing frame and an "ASSIGNED TO YOU" ribbon; descriptions and comments render Markdown; the Comments tab has an **AI comments** filter (Copilot, CodeRabbit, Claude, …).
- **People**: each card now shows raised, assigned, waiting and the oldest wait.
- **AI tool limits** (Settings): add Claude, ChatGPT and others, press *Limit reached* to start the countdown; an alarm and a notification fire when the tool is usable again. (No vendor offers a read-only usage API for subscriptions, so the limit start is marked by hand.)
- **Accounts** show avatar, name, sign-in method and watched-repository count; the repository picker is collapsible.

## Batch 2

- **People office**: avatars walk in and sit at desks; click a person for a full activity dashboard (PRs raised/open/merged/closed, comments, reviews, pending branches, recent PRs and events).
- **SQLite storage** (`gitdash.db`, next to settings): PR snapshots, person activity and timer sessions. On start the saved data shows instantly and a normal sync then updates it (ETag-based). Settings stay as editable JSON. Needs a NuGet restore for `Microsoft.Data.Sqlite`.
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

## Round 7 changes

- **Custom window chrome:** the main window is transparent with a rounded frame, a custom title bar (drag to move, double-click to maximise) and custom minimise / maximise / close buttons with hover animation (fading background, popping glyph; close turns red and spins). Corners square off while maximised. If your GPU/driver draws black corners, remove `TransparencyLevelHint` in `MainWindow.axaml` and set the frame's `CornerRadius` to 0.
- **Notification timeouts are synced:** a notification that ends on the toast (close, click or timeout) also ends on the dynamic island, and vice versa.
- **My PRs buttons** are now three equal-width buttons (Details, Review timer, GitHub) that always fit the card.
- **Steam-style 3D tilt:** the front card in My PRs leans towards the mouse in 3D with a moving glare (`Controls/Tilt.cs`, driven by `PullRequestCardViewModel.IsFront`).
- **Collapsible sidebar:** the arrow button at the bottom of the sidebar collapses it to an icon strip (remembered in settings as `SidebarPinned`). While collapsed it opens automatically over the pages when the mouse is over it and closes when the mouse leaves; the pages do not move.

## Round 8 changes

- **Priority order everywhere:** pull requests assigned to you come first, then ones where you are a requested reviewer, then the rest (`PullRequest.ViewerRank`). Applied in My PRs, the Dashboard desk and repository rows, and the People floors.
- **My PRs waiting times follow the status:** ASSIGNED (cyan) for how long it has been assigned to you, AWAITING REVIEW (amber) for how long your review has been awaited, both when it is both, and a plain AGE otherwise.
- **Card flicker fixed:** the card list is now updated in place (remove / move / insert / replace only what changed) instead of being cleared and refilled, and the 3D tilt follows the pointer on the window against the card's fixed layout rectangle, so tilting no longer makes the card flicker at its edges. The tilt switches off while the details popup is open.
- **Review status on My PRs cards:** the card border is green when the PR is approved, orange when changes were requested, and red when it has not been reviewed; approved PRs also get a green APPROVED tag (on the card and in the details popup).
- **One border per card:** the glowing assigned frame is gone; the border colour shows only the review state, and the status is shown by tags that sit side by side in the card header (cyan ASSIGNED, green APPROVED, plus the re-approval / stale tags).
- **Error tags and state colours (My PRs):** PIPELINE FAILED (checks on the newest commit failed) and MERGE CONFLICT (GitHub reports the branch cannot merge cleanly) tags show on every card and in the details popup; this needs the new `mergeable` and `statusCheckRollup` fields in the GraphQL query (untested against live GitHub). The single card border is now: red = pipeline failed or merge conflict, blue = stale review, green = approved, orange = changes requested, purple = not reviewed (first match wins).

## Batch E: Pull requests polish, people as chips, configurable counters

- **Pull request cards:** one row of buttons (Details, Actions); the GitHub link and the Review timer (play + running clock) are icons at the top. The Actions menu holds Approve, Assign author and Remove me as assignee in their own colours, on the card and in the detail view.
- **People are first-name chips** (`Controls/UserChip.cs`): a round profile picture (initials until it loads) and the first name; the full login is the tooltip and stays in the People tab. `Login.FirstName` handles `anna-becker`, `LenaMueller` and `j.smith` style logins; notifications and waiting-for text use it too.
- **No stray GitHub links:** Dashboard, People, Home, toasts and the island open the detailed review screen of the Pull requests page (`ShellViewModel.OpenPullRequest`); the browser is only a fallback when the pull request is not in the loaded list. GitHub links remain on the card icon and in the detail view.
- **Actions** (CI runs) only list the pull requests on the cards, with an "only the card in front" switch and an Actions tab in the detail popup.
- **Settings** is a category rail (Accounts, Repositories, Notifications, Sounds, Appearance, Widgets and island, Window and tray, AI tool limits) showing one section at a time instead of stacked collapsible cards.
- **Timers** cards are compact; Pomodoro durations moved into a flyout.
- **Island and widget counters:** pick any of Assigned, Approval, Raised, Approved, Comments (unresolved only), Ready to merge, Error (plus All open) and their order under *Settings > Widgets and island*. With more than two on the island, it shows a pair at a time and slides to the next pair every N seconds; hovering shows all of them. Unresolved comments need the new `reviewThreads { isResolved }` field in the list query (untested against live GitHub).

## Automatic updates

- **Version in the sidebar:** the running version (`v1.1.0`, taken from the release tag) sits at the bottom of the sidebar with a one-line update status.
- **How it works:** shortly after start and every six hours GitDash asks the GitHub Releases API for the newest published release (`krrish-soliton/GitDash`; your signed-in token is used when there is one, so a private repository works). If it is newer, GitDash shows a notification and a banner, downloads the installer in the background, verifies the SHA-256 checksum GitHub publishes for the file, counts down 10 seconds ("Restart now" / "Later" are on the banner) and restarts into the new version.
- **Windows:** runs the Inno Setup installer silently over the existing installation (`/SILENT`), which closes GitDash, replaces the files and starts it again. Portable zip copies are only told about the new version.
- **macOS:** after the app quits, a small helper script mounts the disk image for your CPU (arm64 / x64), replaces `GitDash.app` in place (the old copy is restored if the swap fails) and opens it. If GitDash lives in a folder it cannot write to, you are told and drag the new version in yourself.
- **Settings > Updates:** "Check for updates" and an "Update automatically" switch. With it off you are still told about a new version and press *Update now* when you want it.
- Release assets are expected as `GitDash-Setup-<version>.exe` and `GitDash-<version>-osx-arm64.dmg` / `-osx-x64.dmg` (what the Release workflow already produces). Untested against a real release: publish two tags to try it.

## Layout and notification centre

- **Home and Dashboard are centred** in the window (content width is capped, so wide windows no longer leave everything on the left).
- **Overlays keep their place on any screen:** the island, the counter widget and the timer widget are saved as percentages of the main screen, so moving from a Full HD display to a 2K one (or projecting) puts them in the same part of the screen. Positions saved by older versions are converted the next time you move them.
- **Home buttons:** Refresh and Open on GitHub are icon buttons on one row.
- **Notification centre:** the bell in the title bar shows the number of unread notifications. It opens the last 50 notifications with their time; click one to mark it read and open the pull request, use the tick to mark one read, "Mark all read" or "Clear". The list is kept in `notifications.json` in the app data folder, so it survives restarts.
- **Pictures on notifications:** toasts, the island and the list show the profile picture of the person involved (author, commenter, merger) on the corner of the notification icon, when there is one.

## GitHub CLI setup

- **Windows installer:** an optional step, "Install GitHub CLI" (shown only when `gh` is not already on the computer), downloads GitHub's own `gh` installer (`gh_<version>_windows_amd64.msi`, version and SHA-256 pinned at the top of `installer/GitDash.iss`), checks the checksum and installs it after GitDash. Windows asks for permission because `gh` installs for all users. If the download or install fails, GitDash is still installed. Silent installs (the auto-updater) never download anything. To move to a newer `gh`, change `GhVersion` and `GhSha256` in the script.
- **Sign in with GitHub CLI (Settings > Accounts):** one click. If `gh` is installed but not logged in, GitDash runs `gh auth login --web`, shows the one-time code, GitHub CLI opens the browser, and GitDash copies the token into its own store when you finish. If `gh` is missing the message says so and offers "Get GitHub CLI" (on a Mac: `brew install gh`).
- Untested against a real `gh` login from inside the app (the output of `gh` is read to find the code): please try it once on a computer where `gh auth status` fails.
