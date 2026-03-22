'use strict';
const express    = require('express');
const path       = require('path');
const fs         = require('fs');
const { randomUUID } = require('crypto');

const app       = express();
const PORT      = parseInt(process.env.PORT || '5680');
const DATA_FILE = process.env.DATA_FILE || '/data/instances.json';

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

// ── Request log ──────────────────────────────────────────────────────────
app.get('/api/logs', (_req, res) => res.json(reqLog.slice().reverse()));

// ── Self-update: pull latest index.html from GitHub ──────────────────────

const RAW_INDEX_URL =
    'https://raw.githubusercontent.com/jonsjsj/WindowsGSMwebapi/master/dashboard/app/public/index.html';

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
