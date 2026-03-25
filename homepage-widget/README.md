# WGSM Homepage Widgets

Custom API widgets for [Homepage](https://gethomepage.dev/) that connect to your WGSM Web API.

No code changes needed — these use Homepage's built-in `customapi` widget type.

## Setup

1. Open `services.yaml` in this folder.
2. Copy the widget block(s) you want.
3. Paste them into your Homepage `services.yaml` (under a group, e.g. `- Game Servers:`).
4. Replace the three placeholders in every block you paste:

| Placeholder | Replace with |
|---|---|
| `WGSM_HOST` | IP or hostname of your WGSM machine (e.g. `192.168.1.10`) |
| `WGSM_PORT` | Web API port — default is `5000` |
| `YOUR_TOKEN` | Bearer token from WGSM > Web API > API Keys |

---

## Widget Reference

### A — Summary (recommended starting point)
- **Endpoint:** `GET /api/resources/summary`
- **Auth:** required
- **Shows:** online server count, total servers, combined CPU %, total game-server RAM, disk free space
- **Refresh:** 15 s

### B — Server List
- **Endpoint:** `GET /api/servers`
- **Auth:** required
- **Shows:** one row per server — server name on the left, status (Online/Offline/Starting…) on the right
- **Refresh:** 15 s

### C — Performance Details
- **Endpoint:** `GET /api/resources/summary`
- **Auth:** required
- **Shows:** CPU %, RAM used, RAM total (system), disk used/free/total
- **Refresh:** 10 s

### D — Single Server Detail
- **Endpoint:** `GET /api/servers/{id}`
- **Auth:** required
- **Shows:** status, players current/max, CPU %, RAM, current map
- Replace `SERVER_ID` with the numeric ID shown in WGSM (1, 2, 3…)
- Replace the service name (`Valheim`) and icon (`mdi-sword`) to match your game
- **Refresh:** 10 s

### E — Instance Info (no auth)
- **Endpoint:** `GET /api/info`
- **Auth:** NONE — safe to expose on a public page
- **Shows:** instance name, online/total server count, app version
- **Refresh:** 30 s

---

## Multiple WGSM Instances

Each widget points to one WGSM host. To monitor multiple machines (e.g. a TrueNAS server and a Windows PC), add a separate widget block per host with different `WGSM_HOST`/`WGSM_PORT`/`YOUR_TOKEN` values.

```yaml
- Game Servers:
    - WGSM - Main PC:
        widget:
          url: http://192.168.1.10:5000/api/resources/summary
          headers:
            Authorization: Bearer token-for-main-pc
          ...

    - WGSM - TrueNAS:
        widget:
          url: http://192.168.1.20:5000/api/resources/summary
          headers:
            Authorization: Bearer token-for-truenas
          ...
```

---

## Icon Options

Homepage supports [Material Design Icons](https://pictogrammers.com/library/mdi/) with the `mdi-` prefix.
Suggested icons:

| Use | Icon |
|---|---|
| General WGSM | `mdi-gamepad-variant` |
| Server list | `mdi-server-network` |
| Performance | `mdi-chart-line` |
| Minecraft | `mdi-minecraft` |
| Valheim / fantasy | `mdi-sword` |
| CS / shooter | `mdi-target` |
| ARK / survival | `mdi-island` |
