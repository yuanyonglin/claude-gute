'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { run, PROXY } = require('./install-claude-settings.cjs');

function scratch(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'claude-settings-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

test('adds the gate proxy and hooks, keeps everything else, and stays idempotent', t => {
  const dir = scratch(t);
  const file = path.join(dir, 'settings.json');
  fs.writeFileSync(file, JSON.stringify({
    permissions: { defaultMode: 'plan' },
    env: { NO_PROXY: '127.0.0.1,localhost' },
    hooks: { SessionStart: [{ hooks: [{ type: 'command', command: 'orca-agent-hook.exe claude', timeout: 10 }] }] }
  }, null, 2));

  const first = run(['--file', file, '--quiet']);
  assert.equal(first.changed, true);

  const after = JSON.parse(fs.readFileSync(file, 'utf8'));
  assert.equal(after.env.HTTPS_PROXY, PROXY);
  assert.equal(after.env.HTTP_PROXY, PROXY);
  assert.equal(after.env.NO_PROXY, '127.0.0.1,localhost');
  assert.equal(after.permissions.defaultMode, 'plan');
  assert.equal(after.hooks.SessionStart.length, 2);
  assert.equal(after.hooks.SessionStart[0].hooks[0].command, 'orca-agent-hook.exe claude');
  assert.equal(after.hooks.UserPromptSubmit.length, 1);
  assert.match(after.hooks.UserPromptSubmit[0].hooks[0].command, /guard-hook\.cjs/);
  assert.equal(after.hooks.UserPromptSubmit[0].hooks[0].timeout, 60);

  const bytes = fs.readFileSync(file, 'utf8');
  const second = run(['--file', file, '--quiet']);
  assert.equal(second.changed, false);
  assert.equal(fs.readFileSync(file, 'utf8'), bytes);
  assert.equal(fs.readdirSync(dir).filter(f => f.includes('.bak-')).length, 1);
});

test('creates a settings file when Claude Code has none yet', t => {
  const dir = scratch(t);
  const file = path.join(dir, 'settings.json');
  assert.equal(run(['--file', file, '--quiet']).changed, true);
  const created = JSON.parse(fs.readFileSync(file, 'utf8'));
  assert.equal(created.env.HTTPS_PROXY, PROXY);
  assert.deepEqual(Object.keys(created.hooks).sort(), ['SessionStart', 'UserPromptSubmit']);
  assert.equal(fs.readdirSync(dir).filter(f => f.includes('.bak-')).length, 0);
});

test('refuses to rewrite a settings file that is not valid JSON', t => {
  const dir = scratch(t);
  const file = path.join(dir, 'settings.json');
  fs.writeFileSync(file, '{ not json');
  assert.throws(() => run(['--file', file, '--quiet']), /不是有效的 settings JSON/);
  assert.equal(fs.readFileSync(file, 'utf8'), '{ not json');
});
