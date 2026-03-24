# Changelog

All notable changes to WGSM (Windows Game Server Manager) are documented here.

---

## v1.0.47 — Unreleased

### New Features
- **Port Monitor panel** — new HamburgerMenu item showing all game server, Web API, and Docker ports with live local-listen and external-reachability checks (portchecker.co for TCP, Docker `docker ps` for container ports).
- **Auto-Start Servers in Settings** — Startup tab now lists every configured game server with a per-server auto-start toggle; changes write to `WindowsGSM.cfg` immediately.
- **Server Lock (password protection)** — optional password gate on the Start and Edit Config actions; SHA-256 hash stored in registry under `ServerLockEnabled` / `ServerLockHash`.
- **Backup upload endpoint** — `POST /api/servers/{id}/backups/upload` accepts `application/octet-stream` with filename from `X-Backup-Filename` header; used by the TrueNAS dashboard migration feature.

### Bug Fixes
- **IP scope (LAN mode)** — `ScopeBindingMiddleware` now allows link-local (169.254.x.x / APIPA), CGNAT (100.64–100.127.x.x / Tailscale), IPv6 link-local (fe80::/10), and IPv6 unique-local (fc00::/7) — unusual VPN/VM subnets no longer get a 403.

---

## v1.0.46

### Changes
- **AppSettingsPanel** — Settings promoted from flyout to dedicated HamburgerMenu item (index 4); Updates tab moved from Web API panel to the About & Updates tab in the new Settings panel.
- **Hardware acceleration fix** — `System.Windows.Interop.RenderOptions` delegation moved to `MainWindow.ApplyHardwareAcceleration()` to work around WPF wpftmp compilation restriction on `net8.0-windows`.

---

## v1.0.45

### Bug Fixes
- Build pipeline fix for self-contained `win-x64` publish step.

---

## v1.0.44

### Changes
- WgsmDark theme brush tokens (`WgsmBgBrush`, `WgsmSurfaceBrush`, etc.) applied to Dashboard metric cards.

---

## v1.0.43

### Changes
- Donor Connect flow moved to AppSettingsPanel with `OnDonorActivated` bridge on MainWindow.

---

## v1.0.42

### Changes
- `IsOn_SendStatistics` exposed as public property on AppSettingsPanel; all four flyout references in `MainWindow.xaml.cs` replaced.

---

## v1.0.41

### Changes
- AppSettingsPanel UserControl created (General / Startup / About & Updates tabs) replacing the Settings flyout.
- Settings item removed from `OptionsItemsSource` and added as main `ItemsSource` entry (index 4).

---

## v1.0.40

### Changes
- Initial Web API redesign: WebApiSettingsPanel refactored; dark-theme card styles introduced.

---

## v1.0.39

### New Features
- **App Update panel** — Added App Update card to the Web API Settings panel with Check / Apply buttons and full error messages on failure.

---

## v1.0.38

### Bug Fixes
- **WPF thread error on install** — All `gameServer.*` method calls in the install job are now dispatched to the WPF UI thread via `Application.Current.Dispatcher.Invoke`.

---

## v1.0.37

### Bug Fixes
- **GitHub rate limit on update check** — Switched from the GitHub JSON API (`/releases/latest`) to following the HTML redirect, which has no rate limit and requires no API key.

---

## v1.0.36

### New Features
- **Install Wizard** — `POST /api/servers/install` with `PATCH /api/servers/{id}/config` for pre-populating port, query port, max players, and map before first start.
- **Disk stats** — disk free / used shown in the dashboard header.

### Bug Fixes
- CPU PID tracking fixed so CPU usage per server updates correctly after a restart.

---

## v1.0.35

### New Features
- **File Manager** — `GET/POST/DELETE /api/servers/{id}/files` endpoints for browsing, uploading, and deleting server files.
- **Config editor** — `GET/PATCH /api/servers/{id}/config` for reading and writing `WindowsGSM.cfg` values.
- **Backup downloads** — `GET /api/backup/list` and `GET /api/backups/{file}` for downloading backup ZIPs.
- Docker-style sidebar + card UI for the web dashboard.
- Auto-refresh every 5 seconds on the server list.

---

## v1.0.34

### New Features
- **Plugin registry** — 87 game plugins embedded; `GET /api/games` returns all installable games.
- **Plugin install** — `POST /api/servers/install` triggers a background install job with `GET /api/servers/install/{jobId}` polling.

---

## v1.0.33

### New Features
- **Initial Web API release** — Kestrel HTTP server embedded in WGSM; token-based auth (`Authorization: Bearer …`); endpoints for start / stop / restart / update / backup; A2S query support.

---

## v1.0.32

### Changes
- Internal build; stabilisation of Web API bootstrap and dependency injection wiring.

---

## v1.0.31 — Unreleased

### New Features
- **Light & Dark mode** — full application theme switching via the Dark Theme toggle in Settings. All WGSM UI elements (buttons, inputs, grids, sidebar) update instantly when toggled.
- **Expanded sidebar** — hamburger menu is now always open at 200 px, showing icon + label for every nav item (Home, Dashboard, Discord Bot, Web API).
- **Docker dashboard redesign** — web dashboard matches the WGSM dark design language: teal accent (`#00d4aa`), dark surfaces, sidebar nav, card-based server layout, teal/orange progress bars.

### Bug Fixes
- **Connection scope forgotten on restart** — selecting Local / LAN / External scope in the Web API settings panel is now correctly persisted. Root cause: a stale `IsChecked="True"` attribute on the LocalOnly radio button was triggering an `OnScopeChanged` save before the saved config was ever loaded.
- Added `_loading` guard flag so that `OnScopeChanged`, `OnHttpsToggled`, and `OnAutoStartToggled` never write config while `LoadConfigToUi()` is running.

---

## v1.0.30

### Changes
- Plugins flyout redesigned: dark surface, stats bar (installed / loaded / failed), section header, proper `DockPanel` layout without negative-margin hacks.
- New Web API icon in the hamburger menu (teal globe, replaces generic Settings icon).

---

## v1.0.29

### Changes
- Dark UI redesign across the application: WGSM brand theme (`#00D4AA` teal accent, `#0D0D0D` background, `#111111` / `#1A1A1A` surface layers).
- Action buttons restyled: Start (green), Stop (red), Restart (orange), other actions (muted grey).
- Web API settings panel rewritten with horizontal sub-nav tabs: General, API Keys, Connection, Backup, Updates, Log.

---

## v1.0.28

### Bug Fixes
- Plugin install: browser now downloads raw `.cs` content directly and sends `{fileName, content}` to the new `POST /api/plugins/save` endpoint, removing the server-side GitHub fetch.

---

## v1.0.27

### Bug Fixes
- GitHub URL resolver fully client-side — works with any WGSM API version, no server-side dependency.
- Handles any GitHub URL format (blob, tree, raw, short links).

---

## v1.0.26

### Bug Fixes
- Plugin GitHub URL resolver moved entirely to the browser — no longer requires the API server to resolve raw URLs.

---

## v1.0.25

### Bug Fixes
- Plugin search bar unified: handles both keyword search and direct GitHub URL install in the same input field.

---

## v1.0.24

### Bug Fixes
- Plugin install: `/tree/` GitHub URLs now resolve correctly (previously only `/blob/` URLs worked).

---

## v1.0.23

### New Features
- Plugin manual install from a GitHub URL directly in the dashboard plugin browser.

---

## v1.0.22

### New Features
- Plugin browser in the Docker dashboard: search, install, and remove WGSM plugins without opening the desktop app.

---

## v1.0.21

### Changes
- Program display name changed from **WindowsGSM** to **WGSM**.

---

## v1.0.20

### Bug Fixes
- Dashboard now shows diagnostic error messages when game server instances fail to load, instead of silently skipping them.

---

## v1.0.19

### Bug Fixes
- Config and data paths now derive from the executable's directory rather than the temporary extraction folder, so settings persist correctly after updates when running as a single-file publish.

---

## v1.0.18

### Bug Fixes
- Removed ambiguous `System.Windows.Forms` using directive; `FolderBrowserDialog` is now fully qualified to prevent build errors.

---

## v1.0.17

### New Features
- Port management page in the Web API settings panel.
- Backup path configuration (local, OneDrive, Google Drive).
- Self-update from GitHub Releases inside the Web API panel.
- Version check runs automatically on startup and logs the result.

---

## v1.0.16

### Bug Fixes
- API server list now uses `ServerTable.Name` (not the internal `DisplayName`).
- Fixed `out` variable handling in `ConcurrentDictionary` lookups.

---

## v1.0.15

### New Features
- **Multi-key API** — create, name, copy, and revoke multiple API keys.
- **Instance naming** — give your WGSM instance a friendly name shown in the dashboard.
- **Logs endpoint** — `GET /api/logs` streams the current console log.
- **WGSM.WEB Docker dashboard** — self-hosted web UI served by the API; control all servers from a browser.

---

## v1.0.14

### Bug Fixes
- Web API config instance is now shared between the settings panel and the authentication middleware, so token changes take effect immediately without restarting.

---

## v1.0.13

### New Features
- Verbose request/response logging in the Web API log window.

---

## v1.0.12

### New Features
- API start now verifies the port is actually listening and logs the result.
- Port checks for running game servers logged on API start.
- Token shown in plain text in the log for easy copy-paste.

---

## v1.0.11

### New Features
- API key / bearer token shown in plain text in the settings panel.
- Self-update: download and apply the latest WGSM release from GitHub without leaving the app.

---

## v1.0.10

### Bug Fixes
- Removed the NSSM Windows service wrapper — the WPF application cannot run headless as a service; this was causing silent startup failures.

---

## v1.0.0 – v1.0.9

### v1.0.0 — Initial Web API Release
- `GET /api/servers` — list all game server instances with status, ports, and uptime.
- `POST /api/server/{id}/start|stop|restart` — control individual servers.
- Bearer token authentication.
- Kestrel HTTP server embedded inside the WPF process.
- Web API Settings panel with port, scope, and HTTPS configuration.

### v1.0.1 – v1.0.9 — Build & CI Fixes
- Switched CI build from `dotnet publish` to MSBuild to support `ResolveComReference` (required by the Windows Firewall COM interop).
- Fixed missing `using` directives and namespace ambiguities introduced by the new WebApi assemblies.
- Corrected self-contained publish asset pipeline (restore → publish in a single MSBuild call with `--runtime win-x64`).
- Fixed installer launch after setup completing with UAC error 740.
- Fixed double-process launch regression caused by SDK style mismatch.
