'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const dir = __dirname;
const policy = JSON.parse(fs.readFileSync(path.join(dir, 'guard-policy.json')));
const command = process.argv[2] || 'status';
const quiet = process.argv.includes('--quiet');
if (!['status','check','resume','block','stop'].includes(command)) throw new Error('status | check | resume | block | stop');
let token;
try { token = fs.readFileSync(path.join(dir, 'guard-runtime/admin-token'), 'utf8').trim(); }
catch { if (!quiet) console.error('Guard not started. Run start-guard.ps1.'); process.exit(1); }
const read = command === 'status' || command === 'check';
// Freshness bound covers a full bounded verify cycle: interval plus three probe attempts plus slack.
const freshBound = policy.intervalMs + 3 * (policy.timeoutMs + 1000) + 5000;
const req = http.request({ hostname: '127.0.0.1', port: policy.adminPort, path: read ? '/status' : '/' + command,
  method: read ? 'GET' : 'POST', headers: { Authorization: `Bearer ${token}` }, timeout: read ? 12000 : freshBound + 10000 }, res => {
  let body=''; res.on('data', chunk => body+=chunk); res.on('end', () => {
    try {
      const s = JSON.parse(body); console.log(JSON.stringify(s, null, 2));
      const age = Date.now() - Date.parse(s.lastVerified);
      if (res.statusCode !== 200 || (command === 'check' && (s.state !== 'READY' || !Number.isFinite(age) || age < 0 || age > freshBound))) process.exitCode = 1;
    } catch { console.error('Invalid guard response'); process.exitCode=1; }
  });
});
req.on('timeout', () => req.destroy(new Error('control deadline exceeded')));
req.on('error', err => { if (!quiet) console.error('Guard unavailable: ' + err.message); process.exitCode=1; }); req.end();
