'use strict';
const express    = require('express');
const path       = require('path');
const fs         = require('fs');
const { randomUUID } = require('crypto');

const app            = express();
const PORT           = parseInt(process.env.PORT || '5680');
const DATA_FILE      = process.env.DATA_FILE      || '/data/instances.json';
const TEMPLATES_FILE = process.env.TEMPLATES_FILE || '/data/templates.json';

// ── Trust proxy ────────────────────────────────────────────────────────────────
// Required when running behind TrueNAS / Nginx / Traefik so that secure cookies
// and OIDC redirect URLs are generated using the real external host, not localhost.
app.set('trust proxy', 1);

// ── Request log ring-buffer ───────────────────────────────────────────────
const MAX_LOG   = 300;
const reqLog    = [];
function addLog(entry) {
    reqLog.push({ ts: new Date().toISOString(), ...entry });
    if (reqLog.length > MAX_LOG) reqLog.shift();
}
// Log every incoming request
app.use((req, _res, next) => {
    addLog({ type: 'req', method: req.method, path: req.path });
    next();
});

// ── Health check (public — no auth required) ──────────────────────────────────
app.get('/health', (_req, res) => res.json({ ok: true, uptime: process.uptime() }));

// ── OIDC Authentication ────────────────────────────────────────────────────────
// Enabled automatically when all five env vars are present.
// If any are missing the app starts without auth (backward-compatible).
const OIDC_ENABLED = !!(
    process.env.ISSUER_BASE_URL &&
    process.env.CLIENT_ID       &&
    process.env.CLIENT_SECRET   &&
    process.env.BASE_URL        &&
    process.env.SECRET
);

if (OIDC_ENABLED) {
    const { auth } = require('express-openid-connect');
    app.use(auth({
        authRequired:  true,
        auth0Logout:   false,   // use standard OIDC logout, not Auth0-specific endpoint
        idpLogout:     true,    // forward to IdP /end_session on logout
        issuerBaseURL: process.env.ISSUER_BASE_URL,
        baseURL:       process.env.BASE_URL,
        clientID:      process.env.CLIENT_ID,
        clientSecret:  process.env.CLIENT_SECRET,
        secret:        process.env.SECRET,
    }));
    console.log(`OIDC auth enabled  (issuer: ${process.env.ISSUER_BASE_URL})`);
} else {
    console.warn('OIDC auth DISABLED — set ISSUER_BASE_URL, CLIENT_ID, CLIENT_SECRET, BASE_URL and SECRET to enable.');
}

app.use(express.json());
app.use(express.static(path.join(__dirname, 'public')));

// ── Persistence ──────────────────────────────────────────────────────────────

function loadInstances() {
    try {
        if (fs.existsSync(DATA_FILE))
            return JSON.parse(fs.readFileSync(DATA_FILE, 'utf8'));
    } catch {}
    return [];
}

function saveInstances(list) {
    fs.mkdirSync(path.dirname(DATA_FILE), { recursive: true });
    fs.writeFileSync(DATA_FILE, JSON.stringify(list, null, 2));
}

// ── Instance CRUD ─────────────────────────────────────────────────────────────

app.get('/api/instances', (_req, res) => res.json(loadInstances()));

app.post('/api/instances', (req, res) => {
    const { name, url, token } = req.body ?? {};
    if (!name || !url || !token)
        return res.status(400).json({ error: 'name, url and token are required' });

    const list     = loadInstances();
    const instance = { id: randomUUID().split('-')[0], name, url: url.replace(/\/$/, ''), token };
    list.push(instance);
    saveInstances(list);
    res.json(instance);
});

app.put('/api/instances/:id', (req, res) => {
    const list = loadInstances();
    const idx  = list.findIndex(i => i.id === req.params.id);
    if (idx === -1) return res.status(404).json({ error: 'Not found' });
    const { name, url, token } = req.body ?? {};
    if (name)  list[idx].name  = name;
    if (url)   list[idx].url   = url.replace(/\/$/, '');
    if (token) list[idx].token = token;
    saveInstances(list);
    res.json(list[idx]);
});

app.delete('/api/instances/:id', (req, res) => {
    saveInstances(loadInstances().filter(i => i.id !== req.params.id));
    res.json({ ok: true });
});

// ── Proxy to WGSM instances ───────────────────────────────────────────────────

app.all('/api/proxy/:instanceId/*', async (req, res) => {
    const list     = loadInstances();
    const instance = list.find(i => i.id === req.params.instanceId);
    if (!instance) return res.status(404).json({ error: 'Instance not found' });

    const subPath = req.params[0] ?? '';
    const qs      = Object.keys(req.query).length
        ? '?' + new URLSearchParams(req.query).toString()
        : '';
    const url = `${instance.url}/${subPath}${qs}`;

    const opts = {
        method:  req.method,
        headers: { 'Authorization': `Bearer ${instance.token}`, 'Content-Type': 'application/json' },
        signal:  AbortSignal.timeout(10_000),
    };
    if (['POST', 'PUT', 'PATCH'].includes(req.method) && req.body)
        opts.body = JSON.stringify(req.body);

    const t0 = Date.now();
    try {
        const upstream = await fetch(url, opts);
        const ms = Date.now() - t0;
        addLog({ type: 'proxy', method: req.method, url, status: upstream.status, ms });
        const ct = upstream.headers.get('content-type') ?? '';
        if (ct.includes('application/json')) {
            res.status(upstream.status).json(await upstream.json());
        } else {
            res.status(upstream.status).send(await upstream.text());
        }
    } catch (err) {
        const ms = Date.now() - t0;
        const status = err.name === 'TimeoutError' ? 408 : 503;
        addLog({ type: 'error', method: req.method, url, error: err.message, ms });
        res.status(status).json({ error: err.message });
    }
});

// Ping an instance without auth (uses /api/info which is public)
app.get('/api/ping/:instanceId', async (req, res) => {
    const list     = loadInstances();
    const instance = list.find(i => i.id === req.params.instanceId);
    if (!instance) return res.status(404).json({ error: 'Not found' });

    try {
        const r = await fetch(`${instance.url}/api/info`, { signal: AbortSignal.timeout(5_000) });
        const data = await r.json().catch(() => ({}));
        res.json({ online: r.ok, ...data });
    } catch {
        res.json({ online: false });
    }
});

// ── Plugin registry ───────────────────────────────────────────────────────
const REGISTRY_FILE = path.join(__dirname, 'plugins-registry.json');

let _registry = null;
function getRegistry() {
    if (!_registry)
        _registry = JSON.parse(fs.readFileSync(REGISTRY_FILE, 'utf8'));
    return _registry;
}

app.get('/api/registry', (_req, res) => {
    try {
        const data = getRegistry();
        res.json({ items: data, totalCount: data.length });
    } catch (e) {
        res.status(500).json({ error: 'Registry unavailable: ' + e.message });
    }
});

// ── Plugin install (Node downloads .cs, saves to WGSM via api/plugins/save) ──
// Works with any WGSM version. TrueNAS fetches from GitHub; WGSM just writes.
app.post('/api/install-plugin', async (req, res) => {
    const { instanceId, rawUrl, fileName } = req.body ?? {};
    if (!instanceId || !rawUrl || !fileName)
        return res.status(400).json({ error: 'instanceId, rawUrl and fileName are required' });

    const instance = loadInstances().find(i => i.id === instanceId);
    if (!instance) return res.status(404).json({ error: 'Instance not found' });

    // 1. Download .cs from GitHub on the Node side
    let content;
    try {
        const dlr = await fetch(rawUrl, { signal: AbortSignal.timeout(20_000) });
        if (!dlr.ok)
            return res.status(502).json({ error: `Could not download ${fileName}: HTTP ${dlr.status}` });
        content = await dlr.text();
    } catch (e) {
        return res.status(502).json({ error: 'Download failed: ' + e.message });
    }

    // 2. Save to WGSM via api/plugins/save
    try {
        const saveRes = await fetch(`${instance.url}/api/plugins/save`, {
            method:  'POST',
            headers: { 'Authorization': `Bearer ${instance.token}`, 'Content-Type': 'application/json' },
            body:    JSON.stringify({ fileName, content }),
            signal:  AbortSignal.timeout(30_000),
        });
        const data = await saveRes.json().catch(() => ({}));
        addLog({ type: 'install', fileName, status: saveRes.status });
        res.status(saveRes.status).json(data);
    } catch (e) {
        res.status(503).json({ error: 'WGSM unreachable: ' + e.message });
    }
});

// ── Config Templates ──────────────────────────────────────────────────────

function loadTemplates() {
    try {
        if (fs.existsSync(TEMPLATES_FILE))
            return JSON.parse(fs.readFileSync(TEMPLATES_FILE, 'utf8'));
    } catch {}
    return [];
}

function saveTemplates(list) {
    fs.mkdirSync(path.dirname(TEMPLATES_FILE), { recursive: true });
    fs.writeFileSync(TEMPLATES_FILE, JSON.stringify(list, null, 2));
}

// GET /api/templates
app.get('/api/templates', (_req, res) => res.json(loadTemplates()));

// POST /api/templates  body: { name, game, config: {...} }
app.post('/api/templates', (req, res) => {
    const { name, game, config } = req.body ?? {};
    if (!name || !config)
        return res.status(400).json({ error: 'name and config are required' });
    const list = loadTemplates();
    const tpl  = { id: randomUUID().split('-')[0], name, game: game ?? '', config, createdAt: new Date().toISOString() };
    list.push(tpl);
    saveTemplates(list);
    res.json(tpl);
});

// PUT /api/templates/:id  — update name/config
app.put('/api/templates/:id', (req, res) => {
    const list = loadTemplates();
    const idx  = list.findIndex(t => t.id === req.params.id);
    if (idx === -1) return res.status(404).json({ error: 'Not found' });
    const { name, game, config } = req.body ?? {};
    if (name)   list[idx].name   = name;
    if (game)   list[idx].game   = game;
    if (config) list[idx].config = config;
    list[idx].updatedAt = new Date().toISOString();
    saveTemplates(list);
    res.json(list[idx]);
});

// DELETE /api/templates/:id
app.delete('/api/templates/:id', (req, res) => {
    saveTemplates(loadTemplates().filter(t => t.id !== req.params.id));
    res.json({ ok: true });
});

// ── Server Migration ──────────────────────────────────────────────────────
// Brokers a clone/move: backs up source server, downloads the ZIP, uploads
// to destination instance, creates a matching server, and triggers restore.
// Body: { srcInstanceId, srcServerId, dstInstanceId, mode: "clone"|"move" }

app.post('/api/migrate', async (req, res) => {
    const { srcInstanceId, srcServerId, dstInstanceId, mode } = req.body ?? {};
    if (!srcInstanceId || !srcServerId || !dstInstanceId)
        return res.status(400).json({ error: 'srcInstanceId, srcServerId and dstInstanceId are required' });

    const instances = loadInstances();
    const src = instances.find(i => i.id === srcInstanceId);
    const dst = instances.find(i => i.id === dstInstanceId);
    if (!src) return res.status(404).json({ error: 'Source instance not found' });
    if (!dst) return res.status(404).json({ error: 'Destination instance not found' });

    const authSrc = { 'Authorization': `Bearer ${src.token}`, 'Content-Type': 'application/json' };
    const authDst = { 'Authorization': `Bearer ${dst.token}`, 'Content-Type': 'application/json' };

    try {
        // 1. Fetch server config from source
        const cfgRes = await fetch(`${src.url}/api/servers/${srcServerId}/config`, {
            headers: authSrc, signal: AbortSignal.timeout(10_000),
        });
        if (!cfgRes.ok) return res.status(502).json({ error: `Could not fetch source config: HTTP ${cfgRes.status}` });
        const cfg = await cfgRes.json();

        // 2. Trigger backup on source (server must be stopped)
        const bkRes = await fetch(`${src.url}/api/servers/${srcServerId}/backup`, {
            method: 'POST', headers: authSrc, signal: AbortSignal.timeout(10_000),
        });
        if (!bkRes.ok) return res.status(502).json({ error: `Backup failed: HTTP ${bkRes.status}` });

        // 3. Poll for backup completion (up to 5 min)
        let backupFile = null;
        for (let i = 0; i < 60; i++) {
            await new Promise(r => setTimeout(r, 5_000));
            const listRes = await fetch(`${src.url}/api/servers/${srcServerId}/backups`, {
                headers: authSrc, signal: AbortSignal.timeout(10_000),
            });
            if (listRes.ok) {
                const backups = await listRes.json();
                if (Array.isArray(backups) && backups.length > 0) {
                    backupFile = backups[0].name;   // sorted newest-first by WGSM
                    break;
                }
            }
        }
        if (!backupFile) return res.status(504).json({ error: 'Backup did not complete within 5 minutes.' });

        // 4. Download backup ZIP from source
        const dlRes = await fetch(
            `${src.url}/api/servers/${srcServerId}/backups/${encodeURIComponent(backupFile)}/download`,
            { headers: authSrc, signal: AbortSignal.timeout(300_000) }
        );
        if (!dlRes.ok) return res.status(502).json({ error: `Backup download failed: HTTP ${dlRes.status}` });
        const zipBuffer = Buffer.from(await dlRes.arrayBuffer());

        // 5. Upload backup ZIP to destination via multipart form
        const uploadRes = await fetch(`${dst.url}/api/servers/${srcServerId}/backups/upload`, {
            method:  'POST',
            headers: { 'Authorization': `Bearer ${dst.token}`, 'Content-Type': 'application/octet-stream',
                       'X-Backup-Filename': backupFile },
            body:    zipBuffer,
            signal:  AbortSignal.timeout(300_000),
        });
        if (!uploadRes.ok) return res.status(502).json({ error: `Backup upload failed: HTTP ${uploadRes.status}` });

        // 6. Trigger restore of the uploaded ZIP on the destination server
        const restoreRes = await fetch(`${dst.url}/api/servers/${srcServerId}/restore`, {
            method: 'POST', headers: { ...authDst, 'Content-Type': 'application/json' },
            body:   JSON.stringify({ fileName: backupFile }),
            signal: AbortSignal.timeout(120_000),
        });
        if (!restoreRes.ok) return res.status(502).json({ error: `Restore trigger failed: HTTP ${restoreRes.status}` });

        // 7. Apply source config to destination server
        const patchRes = await fetch(`${dst.url}/api/servers/${srcServerId}/config`, {
            method: 'PATCH', headers: authDst,
            body: JSON.stringify({
                serverName:  cfg.serverName,
                serverIp:    cfg.serverIp,
                serverPort:  cfg.serverPort,
                queryPort:   cfg.queryPort,
                maxPlayers:  cfg.maxPlayers,
                serverMap:   cfg.serverMap,
                serverParam: cfg.serverParam,
            }),
            signal: AbortSignal.timeout(10_000),
        });

        addLog({ type: 'migrate', srcInstanceId, srcServerId, dstInstanceId, mode: mode ?? 'clone', backupFile });

        // 7. If move mode, stop and warn about manual deletion
        const note = (mode === 'move')
            ? 'Migration complete. Stop and delete the source server manually in the WGSM UI.'
            : 'Clone complete. Source server untouched.';

        res.json({ ok: true, backupFile, message: note, configApplied: patchRes.ok });
    } catch (err) {
        res.status(503).json({ error: 'Migration failed: ' + err.message });
    }
});

// ── Request log ───────────────────────────────────────────────────────────
app.get('/api/logs', (_req, res) => res.json(reqLog.slice().reverse()));

// ── Self-update: pull latest index.html from GitHub ──────────────────────

const RAW_INDEX_URL =
    'https://raw.githubusercontent.com/jonsjsj/WGSMwebapi/master/dashboard/app/public/index.html';

app.post('/api/self-update', async (_req, res) => {
    try {
        const r = await fetch(RAW_INDEX_URL, { signal: AbortSignal.timeout(15_000) });
        if (!r.ok) return res.status(502).json({ error: `GitHub returned ${r.status}` });
        const html = await r.text();
        const dest = path.join(__dirname, 'public', 'index.html');
        fs.writeFileSync(dest, html);
        res.json({ ok: true, message: 'Dashboard updated — refresh your browser.' });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

// ── SPA fallback ──────────────────────────────────────────────────────────────

app.get('*', (_req, res) =>
    res.sendFile(path.join(__dirname, 'public', 'index.html')));

app.listen(PORT, '0.0.0.0', () =>
    console.log(`WGSM.WEB dashboard running on http://0.0.0.0:${PORT}`));
