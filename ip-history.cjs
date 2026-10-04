'use strict';
// History of fixed-exit IP changes, kept so unplanned changes stand out.
// Kinds: baseline (first record), manual (node picker), external (policy changed outside the picker),
// mismatch (observed exit differed from the expected one), warning (changes too frequent).
const fs = require('node:fs');
const path = require('node:path');

const DAY = 24 * 3600 * 1000;
const LIMITS = { day: 2, week: 3 };
const CHANGES = new Set(['manual', 'external', 'mismatch']);

const fileOf = run => path.join(run, 'ip-history.jsonl');

function read(run) {
  const file = fileOf(run);
  if (!fs.existsSync(file)) return [];
  const lines = fs.readFileSync(file, 'utf8').split('\n').filter(l => l.trim());
  return lines.flatMap((line, i) => {
    try { return [JSON.parse(line)]; }
    catch (e) {
      // Only a torn last line (crash mid-append) is tolerated; corruption elsewhere is reported.
      if (i === lines.length - 1) return [];
      throw new Error(`ip-history.jsonl line ${i + 1} is corrupt: ${e.message}`);
    }
  });
}

function counts(entries, now = Date.now()) {
  const changes = entries.filter(e => CHANGES.has(e.kind)).map(e => Date.parse(e.time));
  const day = changes.filter(t => now - t < DAY).length;
  const week = changes.filter(t => now - t < 7 * DAY).length;
  return { day, week, frequent: day >= LIMITS.day || week >= LIMITS.week };
}

// Appends one entry; a change that makes the rate frequent also appends a warning. Returns the new counts.
function record(run, entry, now = Date.now()) {
  fs.mkdirSync(run, { recursive: true });
  const file = fileOf(run);
  // Drop a torn last line (crash mid-append) before appending, so it never ends up in the middle of the file.
  if (fs.existsSync(file)) {
    const text = fs.readFileSync(file, 'utf8');
    if (text && !text.endsWith('\n')) fs.writeFileSync(file, text.slice(0, text.lastIndexOf('\n') + 1));
  }
  const append = e => fs.appendFileSync(file, JSON.stringify({ time: new Date(now).toISOString(), ...e }) + '\n');
  append(entry);
  const c = counts(read(run), now);
  if (CHANGES.has(entry.kind) && c.frequent) append({ kind: 'warning', day: c.day, week: c.week });
  return c;
}

// Daemon start: compare the configured exit with the last one on record.
function onStart(run, policy, now = Date.now()) {
  const last = read(run).filter(e => e.ip).pop();
  if (!last) return { kind: 'baseline', ...record(run, { kind: 'baseline', ip: policy.expectedIp, label: policy.label }, now) };
  if (last.ip !== policy.expectedIp) {
    return { kind: 'external', ...record(run, { kind: 'external', ip: policy.expectedIp, label: policy.label, previousIp: last.ip }, now) };
  }
  return null;
}

// The same mismatch seen again within an hour (e.g. repeated resume attempts) is one event, not several.
function onMismatch(run, policy, observed, now = Date.now()) {
  const key = [...new Set(observed)].sort().join(',');
  const last = read(run).filter(e => e.kind === 'mismatch').pop();
  if (last && last.observed.join(',') === key && last.expectedIp === policy.expectedIp && now - Date.parse(last.time) < 3600 * 1000) return null;
  return { kind: 'mismatch', ...record(run, { kind: 'mismatch', expectedIp: policy.expectedIp, label: policy.label, observed: key.split(',') }, now) };
}

module.exports = { read, counts, record, onStart, onMismatch, LIMITS };
