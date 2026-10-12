'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { coreExecutable, clashVergeDir, startGuardCommand } = require('./guard-lib.cjs');

test('core executable name per platform', () => {
  assert.match(coreExecutable('base', 'win32'), /mihomo-gate\.exe$/);
  assert.match(coreExecutable('base', 'darwin'), /mihomo-gate$/);
});

test('clash verge profile directory per platform', () => {
  const id = 'io.github.clash-verge-rev.clash-verge-rev';
  assert.equal(clashVergeDir('win32', { APPDATA: 'C:\\Users\\u\\AppData\\Roaming' }, 'C:\\Users\\u'), 'C:\\Users\\u\\AppData\\Roaming\\' + id);
  assert.equal(clashVergeDir('darwin', {}, '/Users/u'), '/Users/u/Library/Application Support/' + id);
  assert.equal(clashVergeDir('linux', {}, '/home/u'), '/home/u/.local/share/' + id);
});

test('session start runs the platform start script', () => {
  const win = startGuardCommand('C:\\gate', 'win32');
  assert.equal(win.file, 'powershell.exe');
  assert.deepEqual(win.args, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\\gate\\start-guard.ps1']);
  assert.deepEqual(startGuardCommand('/opt/gate', 'darwin'), { file: '/bin/sh', args: ['/opt/gate/start-guard.sh'] });
});
