# WindowsGSM Web API — How-To Guide

Embedded REST API + web dashboard for WindowsGSM. Runs inside the WGSM WPF application on Windows. Exposes game servers over HTTP so they can be managed remotely via browser or the TrueNAS dashboard.

**Current version:** v1.0.39

---

## Installation

### Option A — Full Installer (recommended for new installs)

1. Download `WGSM-Full-Setup-{ver}.exe` from the [Releases page](https://github.com/jonsjsj/WindowsGSMwebapi/releases).
2. Run the installer. It installs both WindowsGSM and the Web API.
3. Launch `WindowsGSM.exe`.

### Option B — Addon Installer (existing WGSM install)

1. Download `WGSM-Addon-Setup-{ver}.exe` from Releases.
2. Run the installer on the machine that already has WindowsGSM installed.
3. Restart WindowsGSM.

### Option C — Manual (single exe swap)

1. Download `WindowsGSM.exe` from Releases.
2. Stop WindowsGSM.
3. Replace the existing `WindowsGSM.exe`.
4. Restart.

---

## First-Time Setup

### 1. Enable the Web API

1. Open WindowsGSM.
2. Go to **Settings → Web API**.
3. Set the **Port** (default: `5000`).
4. Set the **Scope**:
   - `LocalOnly` — only the local machine can connect (loopback)
   - `LAN` — any RFC 1918 address (192.168.x.x, 10.x.x.x, 172.16-31.x.x)
   - `External` — all connections allowed
5. Click **Start API**.

### 2. Create an API Key

1. Go to **Web API → API Keys**.
2. Click **Generate**.
3. Copy the token — you will need it to authenticate API calls and to add this instance in the TrueNAS dashboard.

### 3. Open the Built-In Dashboard

Open a browser and navigate to:
```
http://localhost:5000
```
Or replace `localhost` with the machine's LAN IP for remote access.

---

## Built-In Dashboard

The dashboard is served at `GET /` or `GET /ui`. No login required from the browser — auth is handled by the Bearer token at the API level.

### Sidebar Views

| View | Description |
|------|-------------|
| **Servers** | Per-server cards. Shows status (Running/Stopped), CPU%, RAM MB, player count, port reachability. Buttons: Start, Stop, Restart, Update, Backup, Config, Files. |
| **Install Server** | Wizard: pick game, set server name, IP, port, max players, map, launch params. Streams install log. Applies config via PATCH after install completes. |
| **Logs** | Server console output viewer. Auto-refreshes. |
| **Config** | Edit WGSM config per server: ports, max players, auto-flags, Discord webhook. |
| **File Manager** | Browse `serverfiles/` directory, edit text files inline, download any file. |
| **Backup** | List backups. Download backup ZIPs. Trigger restore. |
| **App Update** | Check for and apply WGSM Web API updates. Shows current vs latest version. |
| **Changelog** | Full release history. |

---

## Authentication

All `/api/*` routes (except `/api/info`) require:

```
Authorization: Bearer <your-token>
```

| Route | Auth Required |
|-------|-------------|
| `GET /api/info` | No |
| `GET /ui/*` | No |
| Everything else | Yes — Bearer token |

**HTTP 401** — missing or invalid token
**HTTP 503** — no API keys have been configured yet
**HTTP 403** — your IP is outside the configured Scope

---

## Scope / Firewall Notes

The `ScopeBindingMiddleware` checks the connecting IP against the configured scope:

| Scope | Allowed IPs |
|-------|-------------|
| `LocalOnly` | `127.0.0.1`, `::1` |
| `LAN` | RFC 1918 ranges: `10.x`, `172.16-31.x`, `192.168.x` |
| `External` | All IPs |

If the TrueNAS container and the Windows machine are on the same subnet, use `LAN`. If routing between VLANs or across the internet, use `External`.

> **Note:** Non-standard subnets (e.g. `172.32.x.x`) are not covered by the LAN check and will receive HTTP 403. Use `External` scope in that case.

---

## REST API Reference

### Info

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/info` | None | Returns instance name, version, uptime |
| GET | `/api/status` | Token | API state, bind URL, scope, port |

### Servers

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/servers` | Token | List all servers with status, PID, CPU%, RAM, player count, port reachability |
| GET | `/api/servers/{id}` | Token | Single server details |
| POST | `/api/servers/{id}/start` | Token | Start server |
| POST | `/api/servers/{id}/stop` | Token | Stop server |
| POST | `/api/servers/{id}/restart` | Token | Restart server |
| POST | `/api/servers/{id}/update` | Token | Update game server (server must be stopped) |
| POST | `/api/servers/{id}/backup` | Token | Trigger backup (server must be stopped) |
| GET | `/api/servers/{id}/backups` | Token | List backups (newest first) |
| POST | `/api/servers/{id}/restore` | Token | Restore from backup |
| GET | `/api/servers/{id}/logs` | Token | Last N console lines |
| POST | `/api/servers/{id}/command` | Token | Send console command |
| GET | `/api/servers/{id}/config` | Token | Get WGSM config (ports, players, flags) |
| PATCH | `/api/servers/{id}/config` | Token | Update WGSM config fields (partial update — only non-null fields written) |

### Install

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/games` | Token | List all installable game names (built-ins + loaded plugins) |
| POST | `/api/servers/install` | Token | Start install job. Body: `{ game, serverName }`. Returns `{ jobId, serverId }` |
| GET | `/api/servers/install/{jobId}` | Token | Poll install: `{ status, log[], serverId, error }`. Poll every 2 s until `status === "done"` or `"failed"` |

### Files

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/servers/{id}/files?path=.` | Token | List directory contents |
| GET | `/api/servers/{id}/files/read?path=file.ini` | Token | Read text file (≤ 2 MB) |
| PUT | `/api/servers/{id}/files/write` | Token | Write text file (whitelisted extensions only — see below) |
| GET | `/api/servers/{id}/files/download?path=...` | Token | Download binary file |
| GET | `/api/servers/{id}/backups/{fileName}/download` | Token | Download backup ZIP |

**Writable extensions:** `.ini`, `.cfg`, `.json`, `.yaml`, `.yml`, `.txt`, `.conf`, `.properties`, `.toml`, `.xml`, `.log`

All file paths are validated with `Path.GetFullPath` against `servers/{id}/serverfiles/` to prevent path traversal.

### Resources

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/resources/summary` | Token | CPU%, RAM MB, system total RAM, disk used/free/total GB |

### Plugins

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/plugins/installed` | Token | List installed plugins |
| GET | `/api/plugins/available` | Token | List registry plugins (87 verified) |
| POST | `/api/plugins/install` | Token | Install plugin by registry entry or raw URL |
| POST | `/api/plugins/save` | Token | Save plugin `.cs` file directly (used by TrueNAS broker) |
| DELETE | `/api/plugins/{fileName}` | Token | Remove plugin |
| POST | `/api/plugins/reload` | Token | Hot-reload all plugins |

### Ports / Firewall

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/ports/{port}/status` | Token | Check Windows Firewall rule for port |
| POST | `/api/ports/{port}/open` | Token | Add inbound firewall rule |
| DELETE | `/api/ports/{port}/close` | Token | Remove firewall rule |

### App Self-Update

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/update/check` | Token | Check GitHub for a newer release. Returns `{ currentVersion, latestTag, hasUpdate, downloadUrl }` |
| POST | `/api/update/apply` | Token | Download new exe, stop servers, swap binary, restart. Body: `{ downloadUrl }` |

### Token

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | `/api/token/generate` | None | Generate new API token |
| DELETE | `/api/token/revoke` | Token | Revoke token |

---

## Monitoring: CPU & RAM

The `ResourceMonitorService` samples CPU usage on a 5-second interval per tracked PID.

- CPU data is only available for **running** servers.
- There is a ~5-second delay after a server starts before CPU% appears (first sample needed).
- RAM is reported as current working set in MB.
- Disk stats (used/free/total GB) come from `DriveInfo` on the WGSM installation drive.

---

## Server File Layout

```
WindowsGSM/
  servers/
    {id}/
      serverfiles/    ← game files (File Manager root)
      configs/        ← WGSM config files
  backups/
    {id}/             ← backup ZIPs
```

---

## Updating WGSM Web API

### Via Dashboard (recommended — v1.0.37+)

1. Open the built-in dashboard.
2. Go to **App Update** in the sidebar.
3. Click **Apply Update**.
4. The app downloads the new exe, stops all servers, swaps the binary, and restarts.

### Manual

1. Download `WindowsGSM.exe` from [Releases](https://github.com/jonsjsj/WindowsGSMwebapi/releases).
2. Stop WindowsGSM.
3. Replace `WindowsGSM.exe`.
4. Restart.

> **Note:** Users on versions before v1.0.37 must update manually. The old update check uses the GitHub JSON API which has a 60 req/hr rate limit and returns 403 when exceeded.

---

## Build from Source

### Prerequisites

- Visual Studio 2022 or the .NET 8 SDK
- Windows (WPF requires Windows)

### Build

```bash
dotnet publish WindowsGSM/WindowsGSM.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:PublishSingleFile=true
```

### Release

Push a tag matching `v*.*.*` to trigger the GitHub Actions workflow (`build-installer.yml`). The workflow builds on `windows-latest` using .NET 8 + MSBuild + NSIS and produces:
- `WindowsGSM.exe`
- `WGSM-Full-Setup-{ver}.exe`
- `WGSM-Addon-Setup-{ver}.exe`

---

## Troubleshooting

**HTTP 401 on all API calls**
- No API keys have been generated, or the token is wrong.
- Generate a new key in WGSM → Web API → API Keys.

**HTTP 403 on all API calls**
- Your connecting IP is outside the configured Scope.
- Change Scope to `LAN` or `External` in WGSM → Web API → Settings.

**HTTP 503 on all API calls**
- No API keys configured at all.
- Generate at least one key.

**Install job fails with "calling thread cannot access this object"**
- This was a bug in versions before v1.0.38 where game server calls weren't dispatched to the WPF UI thread.
- Update to v1.0.38 or later.

**Update check returns 403**
- Versions before v1.0.37 hit the GitHub JSON API which rate-limits at 60 req/hr.
- Update manually to v1.0.37+ which uses the HTML redirect endpoint (no rate limit, no API key).

**CPU% always shows 0 or null**
- The server may have only just started. Wait ~5 seconds.
- Was fixed in v1.0.36 — update if on an older version.

**`Assembly.GetExecutingAssembly().Location` returns empty string**
- Expected for single-file executables. The codebase already uses `Process.GetCurrentProcess().MainModule!.FileName` everywhere.
- Do not change this to `GetExecutingAssembly().Location` — it will break update and path resolution.

---

## Release History

| Version | Date | Key Changes |
|---------|------|-------------|
| v1.0.39 | 2026-03-22 | App Update panel in dashboard; full error messages on update failure |
| v1.0.38 | 2026-03-22 | Fixed install — all gameServer calls dispatched to WPF UI thread |
| v1.0.37 | 2026-03-22 | Fixed update check — switched from GitHub JSON API to HTML redirect |
| v1.0.36 | 2026-03-22 | Install wizard with pre-config; CPU PID tracking fixed; disk stats in header |
| v1.0.35 | 2026-03-22 | File manager, config editor, backup downloads, sidebar layout, auto-refresh |
| v1.0.34 | 2026-03-21 | Plugin registry (87 plugins) + install endpoint |
| v1.0.33 | 2026-03-15 | Initial Web API release |
