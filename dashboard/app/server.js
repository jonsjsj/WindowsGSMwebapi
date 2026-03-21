'use strict';
const express    = require('express');
const path       = require('path');
const fs         = require('fs');
const { randomUUID } = require('crypto');

const app       = express();
const PORT      = parseInt(process.env.PORT || '5680');
const DATA_FILE = process.env.DATA_FILE || '/data/instances.json';

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

    try {
        const upstream = await fetch(url, opts);
        const ct = upstream.headers.get('content-type') ?? '';
        if (ct.includes('application/json')) {
            res.status(upstream.status).json(await upstream.json());
        } else {
            res.status(upstream.status).send(await upstream.text());
        }
    } catch (err) {
        const status = err.name === 'TimeoutError' ? 408 : 503;
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

// ── SPA fallback ──────────────────────────────────────────────────────────────

app.get('*', (_req, res) =>
    res.sendFile(path.join(__dirname, 'public', 'index.html')));

app.listen(PORT, '0.0.0.0', () =>
    console.log(`WGSM.WEB dashboard running on http://0.0.0.0:${PORT}`));
