'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const h = require('./ip-history.cjs');
const H = 3600 * 1000;
function tmp(t) { const d = fs.mkdtempSync(path.join(os.tmpdir(), 'iph-')); t.after(() => fs.rmSync(d, { recursive: true, force: true })); return d; }
const policy = (ip, label = 'n') => ({ expectedIp: ip, label });

test('first start is a baseline, not a change; same exit later records nothing', t => {
  const run = tmp(t), now = Date.now();
  assert.equal(h.onStart(run, policy('1.1.1.1'), now).kind, 'baseline');
  assert.equal(h.onStart(run, policy('1.1.1.1'), now + H), null);
  assert.deepEqual(h.counts(h.read(run), now + H), { day: 0, week: 0, frequent: false });
});

test('picker change is manual; an unexplained change at start is external', t => {
  const run = tmp(t), now = Date.now();
  h.onStart(run, policy('1.1.1.1'), now);
  h.record(run, { kind: 'manual', ip: '2.2.2.2', previousIp: '1.1.1.1' }, now + H);
  assert.equal(h.onStart(run, policy('2.2.2.2'), now + 2 * H), null);
  const ext = h.onStart(run, policy('3.3.3.3'), now + 3 * H);
  assert.equal(ext.kind, 'external');
  assert.equal(h.read(run).filter(e => e.kind === 'external').pop().previousIp, '2.2.2.2');
});

test('two changes in 24h warn; three in 7 days warn; old ones age out', t => {
  const run = tmp(t), now = Date.now(), D = 24 * H;
  h.onStart(run, policy('1.1.1.1'), now - 10 * D);
  assert.equal(h.record(run, { kind: 'manual', ip: '2.2.2.2' }, now - 9 * D).frequent, false);
  assert.equal(h.record(run, { kind: 'manual', ip: '3.3.3.3' }, now - 3 * D).frequent, false);
  assert.equal(h.record(run, { kind: 'manual', ip: '4.4.4.4' }, now - 2 * D).frequent, false);
  const c = h.record(run, { kind: 'external', ip: '5.5.5.5' }, now - H);
  assert.deepEqual(c, { day: 1, week: 3, frequent: true });
  assert.equal(h.read(run).pop().kind, 'warning');
  assert.equal(h.record(run, { kind: 'manual', ip: '6.6.6.6' }, now).day, 2);
});

test('repeated identical mismatch within an hour is recorded once', t => {
  const run = tmp(t), now = Date.now(), p = policy('1.1.1.1');
  assert.ok(h.onMismatch(run, p, ['9.9.9.9', '9.9.9.9'], now));
  assert.equal(h.onMismatch(run, p, ['9.9.9.9'], now + 10 * 60 * 1000), null);
  assert.ok(h.onMismatch(run, p, ['8.8.8.8'], now + 20 * 60 * 1000));
  assert.ok(h.onMismatch(run, p, ['9.9.9.9'], now + 2 * H));
  assert.deepEqual(h.read(run).filter(e => e.kind === 'mismatch').map(e => e.observed.join()), ['9.9.9.9', '8.8.8.8', '9.9.9.9']);
});

test('torn last line is tolerated on read and dropped before the next append', t => {
  const run = tmp(t), now = Date.now();
  h.onStart(run, policy('1.1.1.1'), now);
  fs.appendFileSync(path.join(run, 'ip-history.jsonl'), '{"time":"x","ki');
  assert.equal(h.read(run).length, 1);
  h.record(run, { kind: 'manual', ip: '2.2.2.2' }, now);
  assert.deepEqual(h.read(run).map(e => e.kind), ['baseline', 'manual']);
  fs.appendFileSync(path.join(run, 'ip-history.jsonl'), 'garbage\n{"kind":"x"}\n');
  assert.throws(() => h.read(run), /line 3 is corrupt/);
});
