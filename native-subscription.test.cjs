'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { parseShareLinks } = require('./node-selection.cjs');

const b64 = s => Buffer.from(s, 'utf8').toString('base64');

test('parses the legacy base64 shadowsocks form used by the panel', () => {
  const link = 'ss://' + b64('aes-256-gcm:ExamplePass@203.0.113.10:8388') + '#' + encodeURIComponent('Example-SS');
  const [p] = parseShareLinks(b64(link));
  assert.equal(p.type, 'ss');
  assert.equal(p.server, '203.0.113.10');
  assert.equal(p.port, 8388);
  assert.equal(p.cipher, 'aes-256-gcm');
  assert.equal(p.password, 'ExamplePass');
  assert.equal(p.name, 'Example-SS');
});

test('parses a vless REALITY link with flow, sni and short id', () => {
  const link = 'vless://11111111-2222-3333-4444-555555555555@198.51.100.20:443'
    + '?encryption=none&flow=xtls-rprx-vision&security=reality&sni=portal.example.com&fp=chrome&pbk=EXAMPLEPUBLICKEY&sid=051f20c2&type=tcp'
    + '#' + encodeURIComponent('Example-A');
  const [p] = parseShareLinks(b64(link));
  assert.equal(p.type, 'vless');
  assert.equal(p.uuid, '11111111-2222-3333-4444-555555555555');
  assert.equal(p.network, 'tcp');
  assert.equal(p.tls, true);
  assert.equal(p.flow, 'xtls-rprx-vision');
  assert.equal(p.servername, 'portal.example.com');
  assert.equal(p['client-fingerprint'], 'chrome');
  assert.deepEqual(p['reality-opts'], { 'public-key': 'EXAMPLEPUBLICKEY', 'short-id': '051f20c2' });
});

test('parses vmess and trojan links', () => {
  const vmess = 'vmess://' + b64(JSON.stringify({ v: '2', ps: 'Example-V', add: '192.0.2.30', port: '8443', id: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', aid: '0', scy: 'auto', net: 'ws', path: '/ray', tls: 'tls', sni: 'example.net' }));
  const trojan = 'trojan://ExamplePass@192.0.2.40:443?sni=example.org#' + encodeURIComponent('Example-T');
  const list = parseShareLinks(b64([vmess, trojan].join('\n')));
  assert.equal(list.length, 2);
  assert.equal(list[0].type, 'vmess');
  assert.equal(list[0].network, 'ws');
  assert.deepEqual(list[0]['ws-opts'].path, '/ray');
  assert.equal(list[1].type, 'trojan');
  assert.equal(list[1].password, 'ExamplePass');
});

test('rejects text that is neither a share list nor base64 share links', () => {
  assert.throws(() => parseShareLinks(b64('just some text')), /既不是 Clash YAML/);
  assert.throws(() => parseShareLinks(b64('vless://broken')), /没有解析出可用节点/);
});
