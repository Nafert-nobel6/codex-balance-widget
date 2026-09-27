# Codex Balance Widget — implementation contract

## Supported environment

- Windows 11 x64.
- Microsoft Store/MSIX Codex package (`OpenAI.Codex_*`).
- .NET Framework 4.8 WPF, compiled with the Windows in-box compiler.
- C# 5 syntax only. No NuGet or network-installed build dependencies.

## Process model

`CodexBalanceWidget.exe` starts at user sign-in without a visible window. It is a
small supervisor:

1. Poll for a `ChatGPT.exe` process whose executable path belongs to the
   `OpenAI.Codex_*` package.
2. When Codex appears, synchronize the package's `resources\codex.exe` into the
   widget's private `runtime` directory, preserving the last known-good copy.
3. Start a private `codex app-server --listen stdio://` child process.
4. Show the widget unless a visible Codex primary window is maximized.
5. Stop the child process and hide the widget after all Codex package processes
   exit.

The widget never parses, copies, logs, or displays OAuth/API credentials.
Authentication remains owned by the official Codex app-server.

## Data contract

The core assembly exposes the types in `src/CodexBalanceWidget.Core/Contracts.cs`.
The UI must depend only on `IRateLimitSource` and those immutable snapshot
objects.

The app-server client:

- initializes one JSONL RPC connection;
- accepts at most 1 MiB per JSONL message with a recursion limit of 64;
- calls `account/rateLimits/read` immediately and every 30 seconds;
- checks `account/read` with `refreshToken: false` before each quota read;
- observes auth-file metadata every second and `account/updated` notifications;
  invalidates old snapshots and in-flight replies before reconnecting after a change;
- listens for sparse `account/rateLimits/updated` notifications;
- refetches a full snapshot after a notification instead of guessing missing
  fields;
- treats reset-card detail as optional;
- never calls `account/rateLimitResetCredit/consume`.

## Display rules

- Remaining percentage is `clamp(100 - usedPercent, 0, 100)`.
- Classify quota windows by `windowDurationMins`, not by primary/secondary
  position:
  - `300` minutes: 5-hour ring;
  - the longest window of at least one day: weekly/long-period ring.
- The weekly ring is left and the 5-hour ring is right.
- Missing windows render as unavailable, never as 100%.
- Reset cards are sorted by `expiresAt` ascending, with unknown expiries last.
- Two cards fit in the viewport; the list is mouse-wheel scrollable.
- During the final 24 hours, show an `HH:mm` countdown based on the official
  Unix expiry time and update it every minute.
- If only `availableCount` is returned, show the undisclosed remainder without
  inventing expiry details.

## Window and lifecycle rules

- Borderless, non-activating where possible, no taskbar button, no tray icon.
- The passive bubble stays at the bottom of the normal application z-order.
  A deliberate click on the bubble or expanded window promotes it to the
  topmost band without activation; hover-only expansion does not. Automatic
  collapse, hiding, Codex maximization, and Codex exit remove topmost status.
- Starts as a 56 px resident bubble anchored to the primary work area's
  lower-right corner with a 24 DIP right gap and 48 DIP bottom gap. Hovering for 1.5 seconds or clicking
  expands it; 10/15/30/60 second idle presets collapse it again.
- Expanded windows use the current monitor's native work area, a 64 DIP default
  bottom gap (48 DIP minimum), and persisted bounded title-bar offsets. The supported minimum
  size is 240×148 so the compact layout never collapses below its content
  contract.
- The expand/collapse transition keeps the shared avatar origin fixed and
  animates transforms/opacity, with only one physical window-bounds change at
  each transition boundary.
- Hide within 250 ms when any visible primary Codex window is maximized.
- Show again when Codex is minimized, restored to a normal window, or no Codex
  primary window is visible.
- Do not hide for unrelated fullscreen applications.
- Respect per-monitor DPI and taskbar/work-area changes.

## Reliability and privacy

- Single-instance mutex.
- Exponential restart/backoff for app-server failures, capped at 30 seconds.
- Keep the last successful snapshot on transient failure and show a stale/error
  indicator.
- Rotate diagnostic logs; redact values for keys containing token,
  authorization, cookie, secret, or credential.
- Installer and uninstaller are single-user and require no administrator rights.
- Install under `%LOCALAPPDATA%\CodexBalanceWidget`; register only a per-user Run
  entry.
- Uninstall removes the Run entry and installed files but does not touch
  `%USERPROFILE%\.codex`, Codex settings, sessions, or credentials.
