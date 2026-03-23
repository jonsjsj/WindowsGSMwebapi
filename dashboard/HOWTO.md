# WGSM.WEB — TrueNAS Docker Dashboard: How-To Guide

Multi-instance game server dashboard. Runs as a Docker container on TrueNAS Scale (or any Docker host). Aggregates multiple WindowsGSM Web API instances into one unified UI.

---

## Requirements

- Docker / Docker Compose (or TrueNAS Scale Apps)
- One or more Windows machines running **WindowsGSM Web API v1.0.33+**
- Network access from the TrueNAS container to each Windows machine

---

## Quick Start (docker-compose)

1. Copy `docker-compose.yml` to your TrueNAS host.
2. Adjust the volume path (`/mnt/nvme/apps/wgsmdashboard/data`) to a real location on your system.
3. Start the container:

```bash
docker compose up -d
```

4. Open a browser and go to `http://your-truenas-ip:5680`.

The container downloads `server.js`, `index.html`, and `package.json` directly from GitHub on first boot and installs dependencies automatically. No build step required.

---

## Data Persistence

All persistent data lives in the mapped `/data` volume:

| File | Contents |
|------|----------|
| `/data/instances.json` | Saved WGSM instance connections (URL + token) |
| `/data/templates.json` | Saved server config templates |

Back up this directory to preserve your configuration.

---

## Adding a WGSM Instance

1. Open the dashboard in your browser.
2. Click the **Settings** (gear) icon.
3. Click **Add Instance**.
4. Fill in:
   - **Name** — a friendly label (e.g. `Gaming PC`)
   - **URL** — base URL of the WGSM Web API (e.g. `http://192.168.1.50:5000`)
   - **Token** — an API key generated in WGSM under **Web API → API Keys**
5. Click **Save**. The instance appears in the sidebar immediately.

Repeat for each Windows machine.

---

## Dashboard Tabs

| Tab | What it does |
|-----|-------------|
| **Servers** | Aggregated grid of all servers across all instances. Shows status, CPU/RAM bars, player count, port reachability. Start / Stop / Restart / Update / Backup buttons per server. |
| **Install** | Wizard to install a new game server on any instance. Pick instance, search game, set name/IP/port/players/map/launch params, stream install log. |
| **Plugins** | Browse the 87-plugin registry. One-click install to any instance. |
| **Templates** | Save and apply WGSM server config templates. Create from an existing server, apply to any other server. |
| **Migrate** | Clone or move a server between instances. Backs up source → downloads ZIP → uploads to destination → applies config. |
| **Changelog** | Release notes. |

---

## OIDC Authentication (Optional)

The dashboard can be secured behind an OIDC Identity Provider (Authelia, Authentik, Auth0, etc.).

### How it works
- Auth is **disabled by default** — the app starts without it if any env var is missing (backward-compatible).
- When all five env vars below are set, every page redirects unauthenticated users to your IdP login.
- `/health` is always public (no auth required).

### Enable OIDC

In `docker-compose.yml`, uncomment and fill in:

```yaml
environment:
  - ISSUER_BASE_URL=https://auth.example.com      # Your IdP base URL
  - BASE_URL=http://your-truenas-ip:5680           # This app's external URL
  - CLIENT_ID=your-oidc-client-id
  - CLIENT_SECRET=your-oidc-client-secret
  - SECRET=a-random-32-plus-character-string       # Session encryption key
```

### IdP Configuration (example: Authelia / Authentik)

Create an OIDC application in your IdP with:
- **Redirect URI:** `http://your-truenas-ip:5680/callback`
- **Grant type:** Authorization Code
- **Scopes:** `openid profile email`

### Generating a SECRET

```bash
node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
```

---

## Self-Update

The dashboard can update its own `index.html` from GitHub without restarting the container:

1. Open the dashboard.
2. Go to **Settings → Update Dashboard**.
3. Click **Update Now**.

This pulls the latest `index.html` from the `master` branch of `jonsjsj/WindowsGSMwebapi`. Refresh the browser after updating.

---

## Environment Variables Reference

| Variable | Default | Description |
|----------|---------|-------------|
| `PORT` | `5680` | Port the Express server listens on |
| `DATA_FILE` | `/data/instances.json` | Path to instance persistence file |
| `TEMPLATES_FILE` | `/data/templates.json` | Path to templates persistence file |
| `ISSUER_BASE_URL` | *(unset)* | OIDC: IdP base URL |
| `BASE_URL` | *(unset)* | OIDC: This app's external URL |
| `CLIENT_ID` | *(unset)* | OIDC: Client ID |
| `CLIENT_SECRET` | *(unset)* | OIDC: Client secret |
| `SECRET` | *(unset)* | OIDC: Session encryption secret (≥32 chars) |

---

## API Routes (server-side)

All routes are internal — the browser only talks to this container, not directly to WGSM instances.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/health` | Health check (public) |
| GET/POST/PUT/DELETE | `/api/instances` | Manage saved instances |
| ALL | `/api/proxy/:instanceId/*` | Proxy to WGSM Web API |
| GET | `/api/ping/:instanceId` | Test connectivity to an instance |
| GET | `/api/registry` | Plugin registry (87 plugins) |
| POST | `/api/install-plugin` | Download + save plugin to WGSM |
| GET/POST/PUT/DELETE | `/api/templates` | Config templates CRUD |
| POST | `/api/migrate` | Server migration broker |
| GET | `/api/logs` | Request log ring-buffer (last 300) |
| POST | `/api/self-update` | Pull latest index.html from GitHub |

---

## Troubleshooting

**Instance shows offline**
- Check the URL includes the correct port (e.g. `http://192.168.1.50:5000`)
- Verify the WGSM Web API is running on the Windows machine
- Check the connection **Scope** in WGSM (must be LAN or External if TrueNAS is on a different IP)

**Proxy returns 401**
- The saved token is invalid or expired. Re-generate an API key in WGSM → Web API → API Keys and update the instance in Settings.

**Proxy returns 403**
- The WGSM Scope is set to `LocalOnly`. Change it to `LAN` or `External` in the WGSM Web API settings.

**OIDC redirect loop**
- Ensure `BASE_URL` exactly matches the external URL you use to access the dashboard (including port).
- If behind a reverse proxy, make sure `trust proxy` is set (it is, by default in this app).

**Container won't start**
- Check the data volume path exists and is writable by the container user.

---

## Updating the Container

The container pulls files from GitHub at startup. To get the latest version:

```bash
docker compose down && docker compose up -d
```

Or use the in-dashboard self-update for just the frontend (`index.html`).
