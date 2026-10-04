'use strict';
// Keeps this machine's real node data out of anything that leaves it.
// The values to look for are read at run time from the local, gitignored config, so this file holds none of them.
//   node tools/check-secrets.cjs pre-push <remote> <url>   (git hook; ref lines on stdin)
//   node tools/check-secrets.cjs history                    every commit reachable from any ref
//   node tools/check-secrets.cjs dir <path>                 every file under a folder (e.g. a package before sharing)
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');

const root = path.resolve(__dirname, '..');

function readJson(name) {
  const file = path.join(root, name);
  return fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : null;
}

// Real values from guard-node.json and guard-policy.json. Short or generic values are skipped to avoid noise.
function secrets() {
  const node = readJson('guard-node.json') || {}, policy = readJson('guard-policy.json') || {};
  const reality = node['reality-opts'] || {};
  const named = {
    'node uuid': node.uuid, 'node password': node.password, 'REALITY public-key': reality['public-key'],
    'REALITY short-id': reality['short-id'], 'node server': node.server, 'node SNI': node.servername,
    'node name': node.name, 'policy label': policy.label, 'expected exit IP': policy.expectedIp,
    'expected server': policy.expectedServer, 'Clash profile path': policy.sourceFile, 'profile digest': policy.sourceDigest
  };
  const list = [];
  for (const [what, value] of Object.entries(named)) {
    if (typeof value !== 'string' || value.length < 6) continue;
    list.push({ what, value });
    // Node names often end in an emoji; also catch the bare name.
    const bare = value.replace(/[^\x00-\x7F]+$/u, '').trim();
    if (bare !== value && bare.length >= 6) list.push({ what: what + ' (without emoji)', value: bare });
  }
  return list;
}

function git(args, input) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8', input, maxBuffer: 256 * 1024 * 1024 });
}

// Returns [{ where, what }] for every secret found in the given commits' trees and messages.
// One `git grep` per commit (patterns from a UTF-8 file, binaries searched as text).
function scanCommits(commits, list) {
  const leaks = [];
  const patterns = path.join(require('node:os').tmpdir(), `check-secrets-${process.pid}.txt`);
  fs.writeFileSync(patterns, list.map(s => s.value).join('\n') + '\n');
  try {
    for (const commit of commits) {
      const message = git(['log', '-1', '--format=%B', commit]);
      for (const s of list) if (message.includes(s.value)) leaks.push({ where: `${commit.slice(0, 7)} commit message`, what: s.what });
      let out = '';
      try { out = git(['grep', '-a', '-F', '-o', '-f', patterns, commit]); }
      catch (e) { if (e.status !== 1) throw e; } // status 1 = no match
      for (const line of out.split('\n').filter(Boolean)) {
        // "<commit>:<file>:<match>"; values may contain ':' themselves (Windows paths), so match on the suffix.
        const rest = line.slice(commit.length + 1);
        for (const s of list) if (rest.endsWith(':' + s.value)) leaks.push({ where: `${commit.slice(0, 7)} ${rest.slice(0, -s.value.length - 1)}`, what: s.what });
      }
    }
  } finally { fs.rmSync(patterns, { force: true }); }
  return leaks;
}

function scanDir(dir, list) {
  const leaks = [];
  const walk = d => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) { walk(p); continue; }
      // Binaries too: .NET string literals are UTF-16LE, at either byte alignment.
      const buf = fs.readFileSync(p);
      const text = [buf.toString('utf8'), buf.toString('latin1'), buf.toString('utf16le'), buf.subarray(1).toString('utf16le')].join('\n');
      for (const s of list) if (text.includes(s.value)) leaks.push({ where: path.relative(dir, p), what: s.what });
    }
  };
  walk(dir);
  return leaks;
}

// pre-push stdin: "<local ref> <local sha> <remote ref> <remote sha>" per ref.
function commitsToPush(stdin) {
  const zero = /^0+$/;
  const commits = new Set();
  for (const line of stdin.split('\n').filter(Boolean)) {
    const [, localSha, , remoteSha] = line.trim().split(/\s+/);
    if (zero.test(localSha)) continue; // deleting a remote ref sends nothing
    let target = localSha;
    if (git(['cat-file', '-t', localSha]).trim() === 'tag') {
      const tag = git(['cat-file', '-p', localSha]);
      commits.add(localSha); // tag message is scanned like a commit message
      target = git(['rev-parse', `${localSha}^{commit}`]).trim();
    }
    const range = zero.test(remoteSha) ? [target, '--not', '--remotes'] : [`${remoteSha}..${target}`];
    let list = '';
    try { list = git(['rev-list', ...range]); }
    catch { list = git(['rev-list', target, '--not', '--remotes']); } // remote sha unknown locally (e.g. after a rewrite)
    for (const c of list.split('\n').filter(Boolean)) commits.add(c);
  }
  return [...commits];
}

function report(leaks, scope) {
  if (!leaks.length) { console.log(`check-secrets: no local node data found in ${scope}.`); return 0; }
  console.error(`check-secrets: BLOCKED. Local node data found in ${scope}:`);
  for (const l of leaks) console.error(`  ${l.where}: ${l.what}`);
  console.error('Replace the values (use RFC 5737 addresses such as 203.0.113.10 and names like Example-A), amend or rewrite the commits, then push again.');
  return 1;
}

function main() {
  const [mode, arg] = process.argv.slice(2);
  const list = secrets();
  if (!list.length) { console.log('check-secrets: no local guard-node.json / guard-policy.json; nothing to protect.'); return 0; }
  if (mode === 'pre-push') {
    const commits = commitsToPush(fs.readFileSync(0, 'utf8'));
    // Annotated tag objects: scan their message only.
    const leaks = [];
    for (const c of commits) {
      if (git(['cat-file', '-t', c]).trim() === 'tag') {
        const body = git(['cat-file', '-p', c]);
        for (const s of list) if (body.includes(s.value)) leaks.push({ where: `tag ${c.slice(0, 7)}`, what: s.what });
      } else leaks.push(...scanCommits([c], list));
    }
    return report(leaks, `${commits.length} object(s) being pushed`);
  }
  if (mode === 'history') {
    const commits = git(['rev-list', '--all']).split('\n').filter(Boolean);
    return report(scanCommits(commits, list), `${commits.length} commit(s) of history`);
  }
  if (mode === 'dir' && arg) return report(scanDir(path.resolve(arg), list), arg);
  console.error('usage: check-secrets.cjs pre-push | history | dir <path>');
  return 2;
}

if (require.main === module) process.exitCode = main();
module.exports = { secrets, scanDir };
