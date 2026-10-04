'use strict';
const http = require('node:http');
const https = require('node:https');
const tls = require('node:tls');
const net = require('node:net');
const os = require('node:os');
const { EventEmitter } = require('node:events');

function tunnel(port, authority, timeoutMs, onSocket = () => {}) {
  return new Promise((resolve, reject) => {
    const req = http.request({ hostname: '127.0.0.1', port, method: 'CONNECT', path: authority,
      agent: false, headers: { Host: authority } });
    const timer = setTimeout(() => req.destroy(new Error('upstream CONNECT timeout')), timeoutMs);
    req.on('socket', onSocket);
    req.once('error', err => { clearTimeout(timer); reject(err); });
    req.once('connect', (res, socket, head) => {
      clearTimeout(timer);
      if (res.statusCode !== 200) { socket.destroy(); return reject(new Error('upstream CONNECT rejected')); }
      if (head.length) socket.unshift(head);
      resolve(socket);
    });
    req.end();
  });
}

function getViaProxy(port, url, timeoutMs) {
  return new Promise((resolve, reject) => {
    let raw, secure, req, done = false;
    const agent = new https.Agent({ keepAlive: false });
    const finish = (err, body) => {
      if (done) return; done = true; clearTimeout(timer);
      req?.destroy(); secure?.destroy(); raw?.destroy(); agent.destroy();
      err ? reject(err) : resolve(body);
    };
    const timer = setTimeout(() => finish(new Error('probe deadline exceeded')), timeoutMs);
    const target = new URL(url);
    agent.createConnection = (_opts, callback) => {
      tunnel(port, `${target.hostname}:443`, timeoutMs, s => { raw = s; if (done) s.destroy(); })
        .then(socket => {
          if (done) { socket.destroy(); return; }
          secure = tls.connect({ socket, servername: target.hostname, rejectUnauthorized: true });
          secure.once('error', err => finish(err));
          secure.once('secureConnect', () => callback(null, secure));
        }, err => finish(err));
    };
    req = https.get(target, { agent, headers: { 'User-Agent': 'Claude-Exit-Guard/1.0', Connection: 'close' } }, res => {
      if (res.statusCode !== 200) { finish(new Error(`probe HTTP ${res.statusCode}`)); return; }
      let body = '';
      res.on('data', chunk => { body += chunk; if (body.length > 8192) finish(new Error('probe response too large')); });
      res.once('end', () => finish(null, body));
      res.once('error', err => finish(err));
    });
    req.once('error', err => finish(err));
  });
}

async function realProbe(policy) {
  const answers = await Promise.all([
    getViaProxy(policy.corePort, 'https://api.ipify.org/', policy.timeoutMs),
    getViaProxy(policy.corePort, 'https://www.cloudflare.com/cdn-cgi/trace', policy.timeoutMs)
      .then(body => body.match(/^ip=(.+)$/m)?.[1] || '')
  ]);
  if (answers.some(ip => net.isIP(ip.trim()) !== 4 || ip.trim() !== policy.expectedIp)) {
    const err = new Error('exit IP mismatch or invalid IP from verification services');
    // Valid IPv4 answers that differ from the expected exit, for the IP history.
    err.observed = answers.map(ip => ip.trim()).filter(ip => net.isIP(ip) === 4 && ip !== policy.expectedIp);
    throw err;
  }
  return policy.expectedIp;
}

class Guard extends EventEmitter {
  constructor(policy, probe, onBlock = () => {}) {
    super(); this.policy = policy; this.probe = probe; this.onBlock = onBlock;
    this.state = 'STARTING'; this.reason = 'initial verification pending';
    this.lastOk = 0; this.sockets = new Set(); this.pending = null; this.generation = 0;
    this.server = http.createServer((_req, res) => { res.writeHead(405); res.end('HTTPS CONNECT only'); });
    this.server.on('connect', (req, client, head) => this.connect(req, client, head));
    this.server.on('clientError', (_err, socket) => socket.destroy());
  }
  snapshot() {
    return { state: this.state, reason: this.reason, expectedIp: this.policy.expectedIp,
      lastVerified: this.lastOk ? new Date(this.lastOk).toISOString() : null,
      connections: this.sockets.size, pid: process.pid };
  }
  track(socket) {
    this.sockets.add(socket); socket.on('error', () => {});
    socket.once('close', () => this.sockets.delete(socket));
    return socket;
  }
  block(reason) {
    this.generation++;
    const changed = this.state !== 'BLOCKED';
    this.state = 'BLOCKED'; this.reason = reason;
    for (const s of this.sockets) s.destroy();
    if (changed) { this.onBlock(reason); this.emit('state', this.snapshot()); }
  }
  async verify(explicit = false) {
    if (this.pending) return this.pending;
    if (this.state === 'BLOCKED' && !explicit) return false;
    if (explicit) { for (const s of this.sockets) s.destroy(); this.state = 'STARTING'; }
    const generation = this.generation;
    this.pending = (async () => {
      try {
        const ip = await this.probe();
        if (net.isIP(ip) !== 4 || ip !== this.policy.expectedIp) throw new Error('exit IP mismatch or invalid IP');
        if (generation !== this.generation) return false;
        this.lastOk = Date.now(); this.state = 'READY'; this.reason = 'fixed exit verified';
        this.emit('state', this.snapshot()); return true;
      } catch (err) { this.block(err.message); return false; }
      finally { this.pending = null; }
    })();
    return this.pending;
  }
  async fresh() {
    if (this.state !== 'READY') return false;
    // An in-flight bounded verification is itself the freshness check; do not expire it by wall clock.
    if (this.pending) return this.pending;
    const age = Date.now() - this.lastOk;
    if (age < 0 || age > this.policy.intervalMs + this.policy.timeoutMs + 1000) {
      this.block('verification expired (sleep, clock change or stalled monitor)'); return false;
    }
    if (age >= this.policy.intervalMs) return this.verify();
    return true;
  }
  async connect(req, client, head) {
    this.track(client);
    // Never resolve the destination in this process; the dedicated core resolves remotely.
    if (!/^[a-zA-Z0-9][a-zA-Z0-9.-]*:443$/.test(req.url) || !(await this.fresh())) {
      if (!client.destroyed) client.end('HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\n\r\nClaude gate blocked; run gate-control status.');
      return;
    }
    const generation = this.generation;
    try {
      const upstream = await tunnel(this.policy.corePort, req.url, this.policy.timeoutMs, s => {
        this.track(s); client.once('close', () => s.destroy());
      });
      if (client.destroyed || generation !== this.generation || !(await this.fresh())) { upstream.destroy(); client.destroy(); return; }
      client.write('HTTP/1.1 200 Connection Established\r\n\r\n');
      if (head.length) upstream.write(head);
      client.pipe(upstream); upstream.pipe(client);
      upstream.once('close', () => client.destroy());
      this.emit('tunnel', req.url);
    } catch { client.destroy(); this.block('dedicated upstream connection failed'); }
  }
  async listen(port = this.policy.proxyPort) {
    await new Promise((resolve, reject) => {
      this.server.once('error', reject); this.server.listen(port, '127.0.0.1', resolve);
    });
    this.timer = setInterval(() => { this.fresh().catch(err => this.block(err.message)); }, 250);
  }
  async close() {
    clearInterval(this.timer); this.block('guard stopped');
    await new Promise(resolve => this.server.close(resolve));
  }
}
// Bind only to interfaces the user listed, in order. Never pick by default route: TUN adapters hold IPv4 too.
function pickInterface(policy, interfaces = os.networkInterfaces()) {
  const names = [].concat(policy.interfaceName);
  const up = names.find(n => (interfaces[n] || []).some(a => (a.family === 'IPv4' || a.family === 4) && !a.internal));
  if (!up) throw new Error(`configured network interface has no IPv4 address: ${names.join(', ')}`);
  return up;
}
module.exports = { Guard, realProbe, getViaProxy, tunnel, pickInterface };
