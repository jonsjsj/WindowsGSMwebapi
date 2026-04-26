'use strict';
/**
 * TrueNAS GSM API  v2.0
 * Multi-user, port-pool, user-scoped permissions
 */

const express   = require('express');
const fs        = require('fs');
const path      = require('path');
const crypto    = require('crypto');
const { exec }  = require('child_process');
const { promisify } = require('util');
const execAsync = promisify(exec);

const app  = express();
const PORT           = parseInt(process.env.PORT          || '7876');
const DATA_DIR       = process.env.DATA_DIR               || '/data';
const API_TOKEN      = process.env.API_TOKEN              || 'changeme';
const HOST_NAME      = process.env.HOST_NAME              || 'truenas-gsm';
const DATA_ROOT      = process.env.DATA_ROOT              || '/gameservers';
const HOST_DATA_ROOT = process.env.HOST_DATA_ROOT         || DATA_ROOT;
const JWT_SECRET     = process.env.JWT_SECRET             || (API_TOKEN + '-jwt-secret');
const PORT_POOL_START= parseInt(process.env.PORT_POOL_START || '27000');
const PORT_POOL_SIZE = parseInt(process.env.PORT_POOL_SIZE  || '500');

const SERVERS_FILE   = path.join(DATA_DIR, 'servers.json');
const USERS_FILE     = path.join(DATA_DIR, 'users.json');
const MAX_FILE_READ  = 512 * 1024;

app.set('trust proxy', 1);
app.use(express.json({ limit: '4mb' }));
app.use(express.static(path.join(__dirname, 'public')));

// Token helpers
function hashPassword(pw) {
    return crypto.createHmac('sha256', JWT_SECRET).update(pw).digest('hex');
}
function makeToken(userId) {
    var payload = Buffer.from(JSON.stringify({ userId: userId, exp: Date.now() + 86400000 * 30 })).toString('base64url');
    var sig = crypto.createHmac('sha256', JWT_SECRET).update(payload).digest('base64url');
    return payload + '.' + sig;
}
function verifyToken(token) {
    if (!token || !token.includes('.')) return null;
    var parts = token.split('.');
    if (parts.length !== 2) return null;
    var sig = crypto.createHmac('sha256', JWT_SECRET).update(parts[0]).digest('base64url');
    if (sig !== parts[1]) return null;
    try {
        var data = JSON.parse(Buffer.from(parts[0], 'base64url').toString('utf8'));
        if (data.exp < Date.now()) return null;
        return data;
    } catch (e) { return null; }
}

// Data helpers
function loadServers() {
    try {
        if (fs.existsSync(SERVERS_FILE))
            return JSON.parse(fs.readFileSync(SERVERS_FILE, 'utf8'));
    } catch (e) {}
    return [];
}
function saveServers(list) {
    fs.mkdirSync(DATA_DIR, { recursive: true });
    fs.writeFileSync(SERVERS_FILE, JSON.stringify(list, null, 2));
}
function loadUsers() {
    try {
        if (fs.existsSync(USERS_FILE))
            return JSON.parse(fs.readFileSync(USERS_FILE, 'utf8'));
    } catch (e) {}
    return [];
}
function saveUsers(list) {
    fs.mkdirSync(DATA_DIR, { recursive: true });
    fs.writeFileSync(USERS_FILE, JSON.stringify(list, null, 2));
}
function ensureDefaultUsers() {
    var users = loadUsers();
    var changed = false;
    if (!users.find(function(u) { return u.username === 'admin'; })) {
        users.push({
            id: 'admin0', username: 'admin',
            passwordHash: hashPassword('changeme'),
            role: 'admin', assignedServers: ['*'],
            createdAt: new Date().toISOString()
        });
        changed = true;
    }
    if (!users.find(function(u) { return u.username === 'test'; })) {
        users.push({
            id: 'test0', username: 'test',
            passwordHash: hashPassword('test'),
            role: 'user', assignedServers: [],
            createdAt: new Date().toISOString()
        });
        changed = true;
    }
    if (changed) saveUsers(users);
}
function shortId() { return Math.random().toString(36).slice(2, 10); }
function cname(srv) { return 'gsm-' + srv.id; }
function loadTemplates() {
    try { return JSON.parse(fs.readFileSync(path.join(__dirname, 'game-templates.json'), 'utf8')); }
    catch (e) { return []; }
}
async function dockerStatus(cn) {
    try {
        var r = await execAsync("docker inspect --format='{{.State.Status}}' " + cn + ' 2>/dev/null');
        return r.stdout.trim();
    } catch (e) { return 'missing'; }
}
async function dockerLogs(cn, lines) {
    lines = lines || 200;
    try {
        var r = await execAsync('docker logs --tail ' + lines + ' ' + cn + ' 2>&1');
        return r.stdout;
    } catch (e) { return ''; }
}
function wgsmStatus(ds) {
    var map = { 'running':'Started','restarting':'Restarting','created':'Starting',
                'paused':'Stopped','exited':'Stopped','dead':'Stopped','missing':'Stopped' };
    return map[ds] || 'Stopped';
}
function normaliseSrv(srv, dockerSt) {
    var ws = wgsmStatus(dockerSt);
    return Object.assign({}, srv, {
        status: ws, running: ws === 'Started',
        serverPort: srv.port, playersMax: srv.maxPlayers,
        playersCurrent: 0, cpuPercent: 0, ramMb: 0,
        gamePortReachable: ws === 'Started', queryPortReachable: ws === 'Started',
    });
}
function fixPort(p, gamePort, queryPort) {
    if (p === '__game__/tcp')  return (gamePort  || 0) + ':' + (gamePort  || 0) + '/tcp';
    if (p === '__game__/udp')  return (gamePort  || 0) + ':' + (gamePort  || 0) + '/udp';
    if (p === '__query__/udp') return (queryPort || 0) + ':' + (queryPort || 0) + '/udp';
    if (p.includes(':')) return p;
    var parts = p.split('/');
    return parts[0] + ':' + parts[0] + (parts[1] ? '/' + parts[1] : '');
}

// Port pool helpers
async function getUsedPorts() {
    var ports = new Set();
    try {
        var r = await execAsync('docker ps -a --format "{{.Ports}}" 2>/dev/null');
        var re = /:(\d+)->/g, m;
        while ((m = re.exec(r.stdout)) !== null) ports.add(parseInt(m[1]));
    } catch (e) {}
    try {
        var r2 = await execAsync("ss -tuln 2>/dev/null | awk 'NR>1{print $5}' | grep -oE '[0-9]+$'");
        r2.stdout.split('\n').filter(Boolean).forEach(function(p) { ports.add(parseInt(p)); });
    } catch (e) {}
    return ports;
}
async function nextAvailablePort(start, size) {
    start = start || PORT_POOL_START;
    size  = size  || PORT_POOL_SIZE;
    var used = await getUsedPorts();
    for (var p = start; p < start + size; p++) {
        if (!used.has(p)) return p;
    }
    return null;
}

// Auth middleware
function requireAuth(req, res, next) {
    var auth = (req.headers['authorization'] || '').replace(/^Bearer\s+/i, '').trim();
    if (auth === API_TOKEN) {
        req.user = { id: 'api-token', username: 'api', role: 'admin', assignedServers: ['*'] };
        return next();
    }
    var data = verifyToken(auth);
    if (!data) return res.status(401).json({ error: 'Unauthorized' });
    var users = loadUsers();
    var user = users.filter(function(u) { return u.id === data.userId; })[0];
    if (!user) return res.status(401).json({ error: 'User not found' });
    req.user = user;
    next();
}
function requireAdmin(req, res, next) {
    if (req.user && req.user.role === 'admin') return next();
    return res.status(403).json({ error: 'Admin access required' });
}
function canAccessServer(user, serverId) {
    if (!user) return false;
    if (user.role === 'admin') return true;
    if (user.assignedServers && user.assignedServers.includes('*')) return true;
    return user.assignedServers && user.assignedServers.includes(serverId);
}

var installJobs = {};

// Public endpoints
app.get('/health', function(_req, res) {
    res.json({ ok: true, uptime: process.uptime() });
});
app.get('/api/info', async function(_req, res) {
    try {
        var servers = loadServers();
        var statuses = await Promise.all(servers.map(function(srv) {
            return dockerStatus(cname(srv));
        }));
        var onlineServers = statuses.filter(function(st) { return st === 'running'; }).length;
        res.json({
            name: HOST_NAME,
            instanceName: HOST_NAME,
            version: '2.0.0',
            os: 'Linux (TrueNAS Docker)',
            arch: process.arch,
            uptime: process.uptime(),
            totalServers: servers.length,
            onlineServers: onlineServers,
        });
    } catch (e) {
        var servers2 = loadServers();
        res.json({
            name: HOST_NAME,
            instanceName: HOST_NAME,
            version: '2.0.0',
            os: 'Linux (TrueNAS Docker)',
            arch: process.arch,
            uptime: process.uptime(),
            totalServers: servers2.length,
            onlineServers: 0,
        });
    }
});

// Auth endpoints
app.post('/api/auth/login', function(req, res) {
    var body = req.body || {};
    var username = (body.username || '').trim().toLowerCase();
    var password = body.password || '';
    if (!username || !password) return res.status(400).json({ error: 'username and password required' });
    var users = loadUsers();
    var user = users.filter(function(u) { return u.username.toLowerCase() === username; })[0];
    if (!user || user.passwordHash !== hashPassword(password)) {
        return res.status(401).json({ error: 'Invalid credentials' });
    }
    var token = makeToken(user.id);
    res.json({ token: token, user: { id: user.id, username: user.username, role: user.role, assignedServers: user.assignedServers } });
});

app.get('/api/auth/me', requireAuth, function(req, res) {
    var u = req.user;
    res.json({ id: u.id, username: u.username, role: u.role, assignedServers: u.assignedServers });
});

// User management (admin only)
app.get('/api/users', requireAuth, requireAdmin, function(_req, res) {
    var users = loadUsers().map(function(u) {
        return { id: u.id, username: u.username, role: u.role, assignedServers: u.assignedServers, createdAt: u.createdAt };
    });
    res.json(users);
});

app.post('/api/users', requireAuth, requireAdmin, function(req, res) {
    var body = req.body || {};
    var username = (body.username || '').trim();
    var password = body.password || '';
    var role     = body.role === 'admin' ? 'admin' : 'user';
    var assigned = Array.isArray(body.assignedServers) ? body.assignedServers : [];
    if (!username || !password) return res.status(400).json({ error: 'username and password required' });
    var users = loadUsers();
    if (users.find(function(u) { return u.username.toLowerCase() === username.toLowerCase(); })) {
        return res.status(409).json({ error: 'Username already exists' });
    }
    var newUser = {
        id: shortId(), username: username,
        passwordHash: hashPassword(password),
        role: role, assignedServers: assigned,
        createdAt: new Date().toISOString()
    };
    users.push(newUser);
    saveUsers(users);
    res.json({ id: newUser.id, username: newUser.username, role: newUser.role, assignedServers: newUser.assignedServers });
});

app.patch('/api/users/:id', requireAuth, requireAdmin, function(req, res) {
    var users = loadUsers();
    var idx = users.findIndex(function(u) { return u.id === req.params.id; });
    if (idx === -1) return res.status(404).json({ error: 'User not found' });
    var body = req.body || {};
    if (body.password) users[idx].passwordHash = hashPassword(body.password);
    if (body.role === 'admin' || body.role === 'user') users[idx].role = body.role;
    if (Array.isArray(body.assignedServers)) users[idx].assignedServers = body.assignedServers;
    if (body.username) {
        var conflict = users.find(function(u, i) { return i !== idx && u.username.toLowerCase() === body.username.toLowerCase(); });
        if (conflict) return res.status(409).json({ error: 'Username already exists' });
        users[idx].username = body.username;
    }
    saveUsers(users);
    res.json({ id: users[idx].id, username: users[idx].username, role: users[idx].role, assignedServers: users[idx].assignedServers });
});

app.delete('/api/users/:id', requireAuth, requireAdmin, function(req, res) {
    var users = loadUsers();
    var idx = users.findIndex(function(u) { return u.id === req.params.id; });
    if (idx === -1) return res.status(404).json({ error: 'User not found' });
    if (users[idx].username === 'admin') return res.status(400).json({ error: 'Cannot delete admin user' });
    users.splice(idx, 1);
    saveUsers(users);
    res.json({ ok: true });
});

// Port management
app.get('/api/ports/check', requireAuth, async function(req, res) {
    var port = parseInt(req.query.port);
    if (!port || port < 1 || port > 65535) return res.status(400).json({ error: 'Invalid port' });
    var used = await getUsedPorts();
    res.json({ port: port, inUse: used.has(port) });
});

app.get('/api/ports/next', requireAuth, async function(req, res) {
    var start = parseInt(req.query.start || PORT_POOL_START);
    var size  = parseInt(req.query.size  || PORT_POOL_SIZE);
    var port  = await nextAvailablePort(start, size);
    if (!port) return res.status(500).json({ error: 'No available port in range ' + start + '-' + (start + size) });
    res.json({ port: port, poolStart: start, poolSize: size });
});

app.get('/api/ports/range', requireAuth, async function(req, res) {
    var start = parseInt(req.query.start || PORT_POOL_START);
    var size  = parseInt(req.query.size  || PORT_POOL_SIZE);
    var used  = await getUsedPorts();
    var available = [], usedInRange = [];
    for (var p = start; p < start + size; p++) {
        if (used.has(p)) usedInRange.push(p); else available.push(p);
    }
    res.json({
        poolStart: start, poolSize: size,
        available: available.slice(0, 200),
        usedInRange: usedInRange.slice(0, 200),
        availableCount: available.length,
        usedCount: usedInRange.length
    });
});

// Templates
app.get('/api/templates', requireAuth, function(_req, res) { res.json(loadTemplates()); });
app.get('/api/templates/:id', requireAuth, function(req, res) {
    var tpl = loadTemplates().filter(function(t) { return t.id === req.params.id; })[0];
    if (!tpl) return res.status(404).json({ error: 'Not found' });
    res.json(tpl);
});

// Games list
app.get('/api/games', requireAuth, function(_req, res) {
    var items = loadTemplates().map(function(t) { return t.game; });
    res.json({ items: items, totalCount: items.length });
});

// Resources summary
app.get('/api/resources/summary', requireAuth, async function(_req, res) {
    try {
        var mem = await execAsync("awk '/MemTotal/{t=$2}/MemAvailable/{a=$2}END{print t\" \"a}' /proc/meminfo");
        var parts = mem.stdout.trim().split(' ');
        var totalMb = Math.round(parseInt(parts[0]) / 1024);
        var freeMb  = Math.round(parseInt(parts[1]) / 1024);
        var usedMb  = totalMb - freeMb;
        var cpu = await execAsync("awk '{u=$1+$3;t=$1+$2+$3+$4;if(NR==1){pu=u;pt=t}else{printf \"%.1f\",100*(u-pu)/(t-pt)}}' <(grep '^cpu ' /proc/stat) <(sleep 0.5; grep '^cpu ' /proc/stat)");
        var cpuPct = parseFloat(cpu.stdout.trim()) || 0;
        var disk = await execAsync("df -BM /gameservers 2>/dev/null || df -BM / 2>/dev/null");
        var dline = disk.stdout.trim().split('\n').slice(-1)[0].split(/\s+/);
        var diskTotal = parseInt(dline[1]) || 0;
        var diskUsed  = parseInt(dline[2]) || 0;
        res.json({ cpu:{pct:cpuPct}, ram:{totalMb:totalMb,usedMb:usedMb,freeMb:freeMb},
                   disk:{totalMb:diskTotal,usedMb:diskUsed}, hostname:HOST_NAME,
                   totalRamMb:totalMb, usedRamMb:usedMb, cpuPercent:cpuPct });
    } catch (e) {
        res.json({ cpu:{pct:0}, ram:{totalMb:0,usedMb:0,freeMb:0}, disk:{totalMb:0,usedMb:0},
                   hostname:HOST_NAME, totalRamMb:0, usedRamMb:0, cpuPercent:0 });
    }
});

// Stubs
app.get('/api/update/check',     requireAuth, function(_req, res) { res.json({ hasUpdate:false, currentVersion:'2.0.0', latestTag:'2.0.0' }); });
app.post('/api/update/apply',    requireAuth, function(_req, res) { res.json({ ok:true, message:'No update.' }); });
app.get('/api/logs',             requireAuth, function(_req, res) { res.json({ lines:['TrueNAS GSM v2.0'] }); });
app.get('/api/plugins/installed',requireAuth, function(_req, res) { res.json([]); });
app.get('/api/plugins/:file',    requireAuth, function(_req, res) { res.status(404).json({ error:'Not found' }); });
app.post('/api/install-plugin',  requireAuth, function(_req, res) { res.status(501).json({ error:'Not supported' }); });
app.post('/api/migrate',         requireAuth, function(_req, res) { res.json({ ok:true }); });

// Install (async job)
app.post('/api/servers/install', requireAuth, function(req, res) {
    var body = req.body || {};
    var game = body.game, serverName = body.serverName;
    if (!game) return res.status(400).json({ error: 'game is required' });
    var templates = loadTemplates();
    var tpl = null;
    for (var i = 0; i < templates.length; i++) {
        if (templates[i].game.toLowerCase() === game.toLowerCase()) { tpl = templates[i]; break; }
    }
    if (!tpl) return res.status(400).json({ error: 'No template for: ' + game });

    var jobId = shortId();
    installJobs[jobId] = { log:[], status:'running', serverId:null };
    var job = installJobs[jobId];

    var id = shortId();
    var cn = 'gsm-' + id;
    var dataPath = DATA_ROOT      + '/' + id;
    var hostPath = HOST_DATA_ROOT + '/' + id;
    var serverPassword = body.password || '';
    var gamePort  = parseInt(body.port      || tpl.defaultPort);
    var queryPort = parseInt(body.queryPort || tpl.defaultQueryPort);

    (async function() {
        try {
            var usedPorts = await getUsedPorts();
            if (usedPorts.has(gamePort)) {
                var alt = await nextAvailablePort(PORT_POOL_START, PORT_POOL_SIZE);
                if (alt) {
                    job.log.push('[truenas-gsm] Port ' + gamePort + ' in use. Auto-assigned: ' + alt);
                    gamePort = alt;
                }
            }
            var env = Object.assign({}, tpl.defaultEnv);
            if (serverName)     env.SERVER_NAME     = serverName;
            if (serverPassword) env.SERVER_PASSWORD = serverPassword;
            var envArgs  = Object.keys(env).map(function(k) { return '-e "' + k + '=' + env[k] + '"'; }).join(' ');
            var portArgs = (tpl.ports || []).map(function(p) { return '-p ' + fixPort(p, gamePort, queryPort); }).join(' ');
            job.log.push('[truenas-gsm] Pulling image: ' + tpl.image);
            var pull = await execAsync('docker pull ' + tpl.image + ' 2>&1');
            pull.stdout.split('\n').filter(Boolean).forEach(function(l) { job.log.push(l); });
            job.log.push('[truenas-gsm] Creating container: ' + cn);
            await execAsync('mkdir -p "' + dataPath + '"');
            var cmd = ['docker run -d','--name ' + cn,'--restart unless-stopped',
                portArgs, envArgs,'-v "' + hostPath + ':/data"',
                tpl.extraArgs || '', tpl.image].join(' ');
            await execAsync(cmd);
            var srv = {
                id:id, name:serverName||game, game:game, templateId:tpl.id, image:tpl.image,
                port:gamePort, queryPort:queryPort, maxPlayers:tpl.defaultMaxPlayers||20,
                serverName:serverName||game, password:serverPassword||'',
                dataPath:dataPath, hostPath:hostPath,
                autoRestart:true, autoStart:false, autoUpdate:false,
                updateOnStart:false, backupOnStart:false,
                discordAlert:false, discordWebhook:'',
                createdAt:new Date().toISOString()
            };
            var list = loadServers(); list.push(srv); saveServers(list);
            job.log.push('[truenas-gsm] Done! Container: ' + cn + '  Port: ' + gamePort);
            job.status = 'done'; job.serverId = id;
        } catch (err) {
            job.log.push('[truenas-gsm] ERROR: ' + err.message);
            job.status = 'error';
        }
    })();

    res.json({ jobId: jobId });
});

app.get('/api/servers/install/:jobId', requireAuth, function(req, res) {
    var job = installJobs[req.params.jobId];
    if (!job) return res.status(404).json({ error: 'Job not found' });
    res.json({ log:job.log, status:job.status, serverId:job.serverId||null });
});

// Server list
app.get('/api/servers', requireAuth, async function(req, res) {
    var list = loadServers();
    if (req.user.role !== 'admin' && !(req.user.assignedServers||[]).includes('*')) {
        list = list.filter(function(s) { return canAccessServer(req.user, s.id); });
    }
    var enriched = await Promise.all(list.map(async function(srv) {
        var status = await dockerStatus(cname(srv));
        return normaliseSrv(srv, status);
    }));
    res.json(enriched);
});

// Create server
app.post('/api/servers', requireAuth, requireAdmin, async function(req, res) {
    var body = req.body || {};
    var name = body.name, game = body.game;
    if (!name || !game) return res.status(400).json({ error: 'name and game required' });
    var templates = loadTemplates();
    var tpl = body.templateId
        ? templates.filter(function(t) { return t.id === body.templateId; })[0]
        : templates.filter(function(t) { return t.game.toLowerCase() === game.toLowerCase(); })[0];
    if (!tpl) return res.status(400).json({ error: 'No template for: ' + game });
    var id = shortId();
    var cn = 'gsm-' + id;
    var dataPath = DATA_ROOT      + '/' + id;
    var hostPath = HOST_DATA_ROOT + '/' + id;
    var gPort = parseInt(body.port      || tpl.defaultPort);
    var qPort = parseInt(body.queryPort || tpl.defaultQueryPort);
    var usedPorts = await getUsedPorts();
    if (usedPorts.has(gPort)) {
        var alt = await nextAvailablePort(PORT_POOL_START, PORT_POOL_SIZE);
        if (alt) gPort = alt;
    }
    var env = Object.assign({}, tpl.defaultEnv, body.extraEnv||{});
    if (body.serverName)  env.SERVER_NAME     = body.serverName;
    if (body.maxPlayers)  env.MAX_PLAYERS     = String(body.maxPlayers);
    if (body.password)    env.SERVER_PASSWORD = body.password;
    var envArgs  = Object.keys(env).map(function(k) { return '-e "' + k + '=' + env[k] + '"'; }).join(' ');
    var portArgs = (tpl.ports||[]).map(function(p) { return '-p ' + fixPort(p, gPort, qPort); }).join(' ');
    var cmd = ['docker run -d','--name ' + cn,'--restart unless-stopped',
        portArgs, envArgs,'-v "' + hostPath + ':/data"',tpl.extraArgs||'',tpl.image].join(' ');
    try {
        await execAsync('mkdir -p "' + dataPath + '"');
        await execAsync(cmd);
        var srv = { id:id, name:name, game:game, templateId:tpl.id, image:tpl.image,
            port:gPort, queryPort:qPort, maxPlayers:body.maxPlayers||tpl.defaultMaxPlayers||20,
            serverName:body.serverName||name, password:body.password||'',
            dataPath:dataPath, hostPath:hostPath,
            autoRestart:true, autoStart:false, autoUpdate:false,
            updateOnStart:false, backupOnStart:false, discordAlert:false, discordWebhook:'',
            createdAt:new Date().toISOString() };
        var list = loadServers(); list.push(srv); saveServers(list);
        res.json(normaliseSrv(srv, 'created'));
    } catch (err) { res.status(500).json({ error: err.message }); }
});

// Get / delete server
app.get('/api/servers/:id', requireAuth, async function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    res.json(normaliseSrv(srv, await dockerStatus(cname(srv))));
});

app.delete('/api/servers/:id', requireAuth, requireAdmin, async function(req, res) {
    var list = loadServers();
    var idx  = list.findIndex(function(s) { return s.id === req.params.id; });
    if (idx === -1) return res.status(404).json({ error: 'Not found' });
    var cn = cname(list[idx]);
    await execAsync('docker stop ' + cn + ' 2>/dev/null || true').catch(function(){});
    await execAsync('docker rm   ' + cn + ' 2>/dev/null || true').catch(function(){});
    list.splice(idx, 1); saveServers(list);
    res.json({ ok:true });
});

// Lifecycle
async function lifecycle(req, res, action) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    var cn = cname(srv);
    try {
        if (action === 'start') {
            if ((await dockerStatus(cn)) === 'missing')
                return res.status(409).json({ error: 'Container missing -- delete and recreate.' });
            await execAsync('docker start ' + cn);
        } else if (action === 'stop') {
            await execAsync('docker stop ' + cn);
        } else if (action === 'restart') {
            await execAsync('docker restart ' + cn);
        } else if (action === 'update') {
            var tpl = loadTemplates().filter(function(t) { return t.id === srv.templateId; })[0];
            if (!tpl) return res.status(400).json({ error: 'Template not found' });
            await execAsync('docker stop ' + cn + ' 2>/dev/null || true');
            await execAsync('docker rm   ' + cn + ' 2>/dev/null || true');
            await execAsync('docker pull ' + srv.image);
            var ea = Object.keys(tpl.defaultEnv||{}).map(function(k) { return '-e "' + k + '=' + tpl.defaultEnv[k] + '"'; }).join(' ');
            var pa = (tpl.ports||[]).map(function(p) { return '-p ' + fixPort(p, srv.port, srv.queryPort); }).join(' ');
            var hp = srv.hostPath || srv.dataPath;
            await execAsync(['docker run -d','--name ' + cn,'--restart unless-stopped',
                pa, ea,'-v "' + hp + ':/data"',tpl.extraArgs||'',tpl.image].join(' '));
        }
        res.json({ ok:true, status: await dockerStatus(cn) });
    } catch (err) { res.status(500).json({ error: err.message }); }
}

app.post('/api/servers/:id/start',   requireAuth, function(q,r) { lifecycle(q,r,'start');   });
app.post('/api/servers/:id/stop',    requireAuth, function(q,r) { lifecycle(q,r,'stop');    });
app.post('/api/servers/:id/restart', requireAuth, function(q,r) { lifecycle(q,r,'restart'); });
app.post('/api/servers/:id/update',  requireAuth, function(q,r) { lifecycle(q,r,'update');  });
// Console / logs
app.get('/api/servers/:id/console', requireAuth, async function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    var log = await dockerLogs(cname(srv), parseInt(req.query.lines||req.query.count||'200'));
    res.json({ lines: log.split('\n') });
});
app.get('/api/servers/:id/logs', requireAuth, async function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    var log = await dockerLogs(cname(srv), parseInt(req.query.count||req.query.lines||'500'));
    res.json({ lines: log.split('\n') });
});
app.post('/api/servers/:id/send-command', requireAuth, async function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    var command = (req.body||{}).command;
    if (!command) return res.status(400).json({ error: 'command required' });
    var tpl = loadTemplates().filter(function(t) { return t.id === srv.templateId; })[0];
    try {
        if (tpl && tpl.consoleExec) {
            await execAsync('docker exec ' + cname(srv) + ' ' + tpl.consoleExec + ' ' + command);
        } else {
            await execAsync('docker exec -i ' + cname(srv) + ' sh -c \'echo ' + JSON.stringify(command) + ' > /proc/1/fd/0\' 2>/dev/null || true');
        }
        res.json({ ok:true });
    } catch (err) { res.status(500).json({ error: err.message }); }
});

// Config
app.get('/api/servers/:id/config', requireAuth, function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    res.json({
        serverName:srv.serverName||'', serverIp:srv.serverIp||'', serverPort:srv.port,
        queryPort:srv.queryPort, maxPlayers:srv.maxPlayers, serverMap:srv.map||'',
        serverParam:srv.serverParam||'', serverGslt:srv.serverGslt||'', password:srv.password||'',
        cpuPriority:srv.cpuPriority||'normal', autoRestart:srv.autoRestart!==false,
        autoStart:srv.autoStart===true, autoUpdate:srv.autoUpdate===true,
        updateOnStart:srv.updateOnStart===true, backupOnStart:srv.backupOnStart===true,
        discordAlert:srv.discordAlert===true, discordWebhook:srv.discordWebhook||''
    });
});
app.patch('/api/servers/:id/config', requireAuth, function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var list = loadServers();
    var idx  = list.findIndex(function(s) { return s.id === req.params.id; });
    if (idx === -1) return res.status(404).json({ error: 'Not found' });
    var b = req.body||{};
    var fields = { serverName:'serverName',serverIp:'serverIp',serverPort:'port',queryPort:'queryPort',
        maxPlayers:'maxPlayers',serverMap:'map',serverParam:'serverParam',serverGslt:'serverGslt',
        password:'password',cpuPriority:'cpuPriority',autoRestart:'autoRestart',autoStart:'autoStart',
        autoUpdate:'autoUpdate',updateOnStart:'updateOnStart',backupOnStart:'backupOnStart',
        discordAlert:'discordAlert',discordWebhook:'discordWebhook' };
    Object.keys(fields).forEach(function(k) { if (b[k] !== undefined) list[idx][fields[k]] = b[k]; });
    saveServers(list); res.json({ ok:true });
});

// File browser (docker exec)
function containerFileRoot(srv) {
    var tpl = loadTemplates().filter(function(t) { return t.id === srv.templateId; })[0];
    return (tpl && tpl.fileRoot) ? tpl.fileRoot : '/data';
}
function safeContainerPath(root, reqPath) {
    var rel  = (reqPath||'').replace(/^\/+/,'');
    var full = path.posix.resolve(root, rel);
    if (!full.startsWith(root)) return null;
    return full;
}

app.get('/api/servers/:id/files', requireAuth, async function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    var cn = cname(srv), root = containerFileRoot(srv);
    var target = safeContainerPath(root, req.query.path||'');
    if (!target) return res.status(400).json({ error: 'Invalid path' });
    try {
        var r = await execAsync('docker exec ' + cn + ' find ' + target + ' -maxdepth 1 -mindepth 1 -printf "%f\t%y\t%s\t%T@\n" 2>/dev/null | sort -t"\t" -k2,2 -k1,1');
        var entries = r.stdout.trim().split('\n').filter(Boolean).map(function(line) {
            var parts = line.split('\t');
            var mts = parseFloat(parts[3]||'0') * 1000;
            return { name:parts[0]||'', isDir:parts[1]==='d', size:parseInt(parts[2]||'0'), modified:mts?new Date(mts).toISOString():null };
        });
        res.json({ entries:entries, path:req.query.path||root });
    } catch (e) { res.status(404).json({ error: 'Directory not found or container not running.' }); }
});

app.get('/api/servers/:id/files/read', requireAuth, async function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    var cn = cname(srv), root = containerFileRoot(srv);
    var target = safeContainerPath(root, req.query.path||'');
    if (!target) return res.status(400).json({ error: 'Invalid path' });
    try {
        var stat = await execAsync('docker exec ' + cn + ' stat -c "%s %Y" ' + target + ' 2>/dev/null');
        var sp = stat.stdout.trim().split(' ');
        var size = parseInt(sp[0]||'0');
        if (size > MAX_FILE_READ) return res.status(400).json({ error: 'File too large.' });
        var read = await execAsync('docker exec ' + cn + ' cat ' + target + ' 2>/dev/null');
        var buf = Buffer.from(read.stdout);
        for (var i = 0; i < Math.min(buf.length, 512); i++) {
            if (buf[i] === 0) return res.status(400).json({ error: 'Binary file.' });
        }
        res.json({ content:buf.toString('utf8'), sizeBytes:size, modified:new Date(parseInt(sp[1]||'0')*1000).toISOString() });
    } catch (e) { res.status(404).json({ error: 'File not found.' }); }
});

app.put('/api/servers/:id/files/write', requireAuth, async function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    var cn = cname(srv), root = containerFileRoot(srv);
    var body = req.body||{};
    var target = safeContainerPath(root, body.path||'');
    if (!target) return res.status(400).json({ error: 'Invalid path' });
    try {
        var content = body.content||'';
        var child = require('child_process').spawn('docker',['exec','-i',cn,'tee',target],{stdio:['pipe','ignore','pipe']});
        child.stdin.end(Buffer.from(content,'utf8'));
        child.on('close', function(code) {
            if (code !== 0) return res.status(500).json({ error: 'Write failed (exit ' + code + ')' });
            res.json({ ok:true });
        });
        child.on('error', function(e) { res.status(500).json({ error: e.message }); });
    } catch (e) { res.status(500).json({ error: e.message }); }
});

// Backups
app.post('/api/backup/create', requireAuth, async function(req, res) {
    var sid = (req.body||{}).serverId;
    var srv = loadServers().filter(function(s) { return s.id === sid; })[0];
    if (!srv) return res.status(400).json({ error: 'serverId required' });
    if (!canAccessServer(req.user, srv.id)) return res.status(403).json({ error: 'Access denied' });
    var ts = new Date().toISOString().replace(/[:.]/g,'-');
    var name = srv.id + '-' + ts + '.tar.gz';
    var dest = path.join(DATA_DIR,'backups',srv.id);
    try {
        await execAsync('mkdir -p "' + dest + '"');
        await execAsync('tar -czf "' + path.join(dest,name) + '" -C "' + srv.dataPath + '" . 2>/dev/null || true');
        res.json({ ok:true, name:name });
    } catch (e) { res.status(500).json({ error: e.message }); }
});
app.get('/api/backup/list', requireAuth, function(req, res) {
    var sid = req.query.serverId;
    var list = loadServers();
    if (sid) list = list.filter(function(s) { return s.id === sid; });
    var all = [];
    list.forEach(function(srv) {
        var dir = path.join(DATA_DIR,'backups',srv.id);
        if (!fs.existsSync(dir)) return;
        fs.readdirSync(dir).filter(function(f) { return f.endsWith('.tar.gz'); }).forEach(function(f) {
            var st = fs.statSync(path.join(dir,f));
            all.push({ serverId:srv.id, serverName:srv.name, name:f, size:st.size, createdAt:st.mtime.toISOString() });
        });
    });
    all.sort(function(a,b) { return b.createdAt.localeCompare(a.createdAt); });
    res.json(all);
});
app.post('/api/servers/:id/backup', requireAuth, async function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    var ts = new Date().toISOString().replace(/[:.]/g,'-');
    var name = srv.id + '-' + ts + '.tar.gz';
    var dest = path.join(DATA_DIR,'backups',srv.id);
    try {
        await execAsync('mkdir -p "' + dest + '"');
        await execAsync('tar -czf "' + path.join(dest,name) + '" -C "' + srv.dataPath + '" . 2>/dev/null || true');
        res.json({ ok:true, name:name });
    } catch (e) { res.status(500).json({ error: e.message }); }
});
app.get('/api/servers/:id/backups', requireAuth, function(req, res) {
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    var dir = path.join(DATA_DIR,'backups',srv.id);
    var files = [];
    if (fs.existsSync(dir)) {
        files = fs.readdirSync(dir).filter(function(f) { return f.endsWith('.tar.gz'); }).map(function(f) {
            var st = fs.statSync(path.join(dir,f));
            return { name:f, size:st.size, createdAt:st.mtime.toISOString() };
        }).sort(function(a,b) { return b.createdAt.localeCompare(a.createdAt); });
    }
    res.json(files);
});

// Stats
app.get('/api/servers/:id/stats', requireAuth, async function(req, res) {
    if (!canAccessServer(req.user, req.params.id)) return res.status(403).json({ error: 'Access denied' });
    var srv = loadServers().filter(function(s) { return s.id === req.params.id; })[0];
    if (!srv) return res.status(404).json({ error: 'Not found' });
    try {
        var result = await execAsync('docker stats ' + cname(srv) + ' --no-stream --format "{{json .}}"');
        res.json(JSON.parse(result.stdout.trim()));
    } catch (e) { res.json({}); }
});

// Start
ensureDefaultUsers();
app.listen(PORT, '0.0.0.0', function() {
    console.log('TrueNAS GSM API v2.0  port=' + PORT + '  host=' + HOST_NAME);
});

