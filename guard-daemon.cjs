'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const net = require('node:net');
const crypto = require('node:crypto');
const { spawn, spawnSync } = require('node:child_process');
const yaml = require('./vendor/yaml');
const { Guard, realProbe, pickInterface } = require('./guard-lib.cjs');
const history = require('./ip-history.cjs');
const dir = __dirname;
const run = path.join(dir, 'guard-runtime');
fs.mkdirSync(run, { recursive: true });
const policyPath = path.join(dir, 'guard-policy.json');
const policyBytes = fs.readFileSync(policyPath);
const policy = JSON.parse(policyBytes);
const pinnedPath = path.join(dir, 'guard-node.json');
const pinnedBytes = fs.readFileSync(pinnedPath);
const node = JSON.parse(pinnedBytes);
if (net.isIP(policy.expectedIp) !== 4 || node.server !== policy.expectedServer || net.isIP(node.server) !== 4) throw new Error('invalid pinned node/policy');
if (![].concat(policy.interfaceName).every(n => typeof n === 'string' && n) || !policy.interfaceName.length || policy.intervalMs < 1000 || policy.timeoutMs < 1000) throw new Error('invalid guard policy');
const latchPath = path.join(run, 'blocked.json');
const runningPath = path.join(run, 'running.json');
const uncleanPreviousRun = fs.existsSync(runningPath);
const tokenPath = path.join(run, 'admin-token');
if (!fs.existsSync(tokenPath)) fs.writeFileSync(tokenPath, crypto.randomBytes(32).toString('hex'), { flag: 'wx' });
const token = fs.readFileSync(tokenPath, 'utf8').trim();
function log(event, details = {}) {
  const file = path.join(run, 'events.jsonl');
  if (fs.existsSync(file) && fs.statSync(file).size > 2 * 1024 * 1024) fs.renameSync(file, file + '.previous');
  fs.appendFileSync(file, JSON.stringify({ time: new Date().toISOString(), event, ...details }) + '\n');
}
function sameFiles() {
  return fs.readFileSync(policyPath).equals(policyBytes) && fs.readFileSync(pinnedPath).equals(pinnedBytes);
}
let core, guard, admin, iface, stopping = false;
// History problems are logged as events but never stop or weaken the guard itself.
function noteHistory(update, event, details) {
  try {
    const c = update();
    if (!c) return;
    log(event, { ...details, kind: c.kind, day: c.day, week: c.week });
    if (c.frequent) log('IP_WARNING', { day: c.day, week: c.week });
  } catch (e) { log('HISTORY_ERROR', { error: e.message }); }
}
async function main() {
  guard = new Guard(policy, async () => {
    if (!core || core.exitCode !== null || !sameFiles()) throw new Error('core unavailable or pinned configuration changed');
    // Transient upstream drops must not freeze Claude; a real exit-IP mismatch still fails immediately.
    let last;
    for (let attempt = 1; attempt <= 3; attempt++) {
      // The core stays bound to the interface chosen at start; name it when that interface loses its address.
      try { pickInterface({ interfaceName: iface }); return await realProbe(policy); }
      catch (e) {
        last = e;
        if (e.observed && e.observed.length) noteHistory(() => history.onMismatch(run, policy, e.observed), 'IP_MISMATCH', { observed: e.observed });
        if (/mismatch|invalid/.test(e.message)) throw e;
        log('PROBE_RETRY', { attempt, error: e.message });
        await new Promise(r => setTimeout(r, 800));
      }
    }
    throw last;
  }, reason => { fs.writeFileSync(latchPath, JSON.stringify({ time: new Date().toISOString(), reason })); log('BLOCKED', { reason }); });
  admin = http.createServer(async (req, res) => {
    res.setHeader('Content-Type', 'application/json');
    if (req.headers.authorization !== `Bearer ${token}`) { res.writeHead(403); res.end('{}'); return; }
    if (req.method === 'GET' && req.url === '/status') { res.end(JSON.stringify({ ...guard.snapshot(), corePid: core?.pid })); return; }
    if (req.method === 'POST' && req.url === '/resume') {
      if (fs.existsSync(path.join(run, 'configuration-pending.json'))) { res.writeHead(503); res.end('{"error":"configuration transaction incomplete"}'); return; }
      const ok = await guard.verify(true);
      if (ok) { fs.rmSync(latchPath, { force: true }); log('MANUAL_RESUME'); }
      res.writeHead(ok ? 200 : 503); res.end(JSON.stringify(guard.snapshot())); return;
    }
    // Non-explicit re-verification: never lifts a block, only confirms or blocks a READY guard.
    if (req.method === 'POST' && req.url === '/verify') {
      const ok = await guard.verify();
      res.writeHead(ok ? 200 : 503); res.end(JSON.stringify(guard.snapshot())); return;
    }
    if (req.method === 'POST' && req.url === '/block') { guard.block('manual block'); res.end(JSON.stringify(guard.snapshot())); return; }
    if (req.method === 'POST' && req.url === '/stop') { res.end('{"stopping":true}'); setImmediate(shutdown); return; }
    res.writeHead(404); res.end('{}');
  });
  await new Promise((resolve, reject) => { admin.once('error', reject); admin.listen(policy.adminPort, '127.0.0.1', resolve); });
  await guard.listen(); // Bind before spawning: duplicate daemon cannot start another core.
  fs.writeFileSync(runningPath, JSON.stringify({ pid: process.pid, started: new Date().toISOString() }));
  // Refuse an occupied backend instead of trusting an unrelated local proxy.
  await new Promise((resolve, reject) => {
    const check = net.createServer(); check.once('error', () => reject(new Error('dedicated core port is already occupied')));
    check.listen(policy.corePort, '127.0.0.1', () => check.close(resolve));
  });
  iface = pickInterface(policy);
  const config = {
    mode: 'rule', 'log-level': 'warning', ipv6: false, 'allow-lan': false, 'bind-address': '127.0.0.1',
    'mixed-port': policy.corePort, 'interface-name': iface,
    'find-process-mode': 'off', 'tcp-concurrent': true,
    proxies: [{ ...node, name: 'PINNED' }], rules: ['MATCH,PINNED'],
    dns: { enable: true, ipv6: false, 'enhanced-mode': 'redir-host', 'use-hosts': false, 'use-system-hosts': false,
      'default-nameserver': ['1.1.1.1'], nameserver: ['https://1.1.1.1/dns-query#PINNED','https://1.0.0.1/dns-query#PINNED'],
      'proxy-server-nameserver': ['rcode://refused'], 'nameserver-policy': {}, fallback: [] }
  };
  // IP-literal proxy and DoH endpoints need no bootstrap DNS. No TUN, DNS listener or controller.
  const cfg = path.join(run, 'core.yaml'); fs.writeFileSync(cfg, yaml.stringify(config));
  const exe = path.join(dir, 'mihomo-gate.exe');
  const args = ['-d', run, '-f', cfg];
  const validation = spawnSync(exe, [...args, '-t'], { windowsHide: true, timeout: 10000 });
  if (validation.status !== 0) throw new Error('dedicated core config validation failed');
  core = spawn(exe, args, { windowsHide: true, stdio: ['ignore','ignore','ignore'] });
  core.once('error', () => guard.block('dedicated core failed to start'));
  core.once('exit', () => { if (!stopping) guard.block('dedicated core exited'); });
  log('START', { pid: process.pid, corePid: core.pid, expectedIp: policy.expectedIp, interface: iface });
  noteHistory(() => history.onStart(run, policy), 'IP_CHANGE', { ip: policy.expectedIp, label: policy.label });
  // Bounded readiness wait does not touch any existing proxy listener.
  for (let i = 0; i < 40; i++) {
    const ready = await new Promise(resolve => {
      const s = net.connect(policy.corePort, '127.0.0.1');
      s.once('connect', () => { s.destroy(); resolve(true); });
      s.once('error', () => { s.destroy(); resolve(false); });
    });
    if (ready) break;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  if (fs.existsSync(latchPath) || uncleanPreviousRun || fs.existsSync(path.join(run, 'configuration-pending.json'))) guard.block('persisted block or unclean shutdown: explicit resume required');
  else await guard.verify();
  let lastState = guard.state;
  guard.on('state', state => { if (state.state !== lastState) { log(state.state); lastState = state.state; } });
  guard.on('tunnel', authority => log('CONNECT', { authority }));
}
async function shutdown() {
  if (stopping) return; stopping = true;
  if (guard) await guard.close();
  fs.rmSync(runningPath, { force: true });
  core?.kill(); admin?.close();
  setTimeout(() => process.exit(0), 200).unref();
}
process.on('SIGINT', shutdown); process.on('SIGTERM', shutdown);
main().catch(err => { console.error(err.message); log('FATAL', { reason: err.message }); guard?.block('daemon startup failed'); core?.kill(); process.exit(1); });
