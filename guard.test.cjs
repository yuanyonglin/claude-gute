'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const net = require('node:net');
const { Guard, tunnel, pickInterface } = require('./guard-lib.cjs');
const delay = ms => new Promise(r => setTimeout(r, ms));
async function fixture(t) {
  const sockets = new Set();
  let hits = 0, response = '203.0.113.10', fail = false;
  const upstream = http.createServer((_req, res) => res.end('normal-network-canary'));
  upstream.on('connection', s => { sockets.add(s); s.on('error',()=>{}); s.on('close',()=>sockets.delete(s)); });
  upstream.on('connect', (_req, s, head) => {
    hits++; s.write('HTTP/1.1 200 Connection Established\r\n\r\n');
    if(head.length) s.write(head); s.on('data', b => s.write(b));
  });
  await new Promise(r => upstream.listen(0,'127.0.0.1',r));
  const policy={ expectedIp:'203.0.113.10', corePort:upstream.address().port, intervalMs:100, timeoutMs:100 };
  const g=new Guard(policy, async()=>{ if(fail) throw Error('probe timed out'); return response; });
  await g.listen(0);
  t.after(async()=>{ await g.close(); for(const s of sockets)s.destroy(); await new Promise(r=>upstream.close(r)); });
  return {g, port:g.server.address().port, policy, upstream, sockets, hits:()=>hits,
    ip:x=>response=x, fail:()=>fail=true, recover:()=>fail=false};
}
function connectStatus(port, target='example.com:443') {
  return new Promise((resolve,reject)=>{
    const req=http.request({hostname:'127.0.0.1',port,method:'CONNECT',path:target,agent:false});
    req.on('connect',(res,s)=>{s.destroy();resolve(res.statusCode)});req.on('error',reject);req.end();
  });
}
test('startup blocks before first valid check; correct IP opens real CONNECT tunnel',async t=>{
  const f=await fixture(t);assert.equal(await connectStatus(f.port),503);assert.equal(f.hits(),0);
  assert.equal(await f.g.verify(),true);
  const s=await tunnel(f.port,'example.com:443',500);t.after(()=>s.destroy());
  const data=new Promise(r=>s.once('data',r));s.write('e2e-payload');assert.equal((await data).toString(),'e2e-payload');
});
test('wrong IP never opens target tunnel',async t=>{
  const f=await fixture(t);f.ip('203.0.113.99');assert.equal(await f.g.verify(),false);
  assert.equal(await connectStatus(f.port),503);assert.equal(f.hits(),0);
});
test('invalid IP and probe error fail closed',async t=>{
  const f=await fixture(t);f.ip('<html>error</html>');assert.equal(await f.g.verify(),false);
  f.fail();assert.equal(await f.g.verify(true),false);assert.equal(f.g.state,'BLOCKED');
});
test('runtime drift closes established stream and latches until explicit resume',async t=>{
  const f=await fixture(t);await f.g.verify();const s=await tunnel(f.port,'example.com:443',500);
  const closed=new Promise(r=>s.once('close',r));s.on('error',()=>{});f.ip('203.0.113.99');
  const started=Date.now();await Promise.race([closed,delay(1200).then(()=>{throw Error('stream remained open')})]);
  assert.ok(Date.now()-started<1200);assert.equal(f.g.state,'BLOCKED');
  f.ip(f.policy.expectedIp);assert.equal(await f.g.verify(),false);assert.equal(await connectStatus(f.port),503);
  assert.equal(await f.g.verify(true),true);assert.equal(await connectStatus(f.port),200);
});
test('timeout interrupts active stream without disturbing normal network canary',async t=>{
  const f=await fixture(t);await f.g.verify();const s=await tunnel(f.port,'example.com:443',500);s.on('error',()=>{});
  const closed=new Promise(r=>s.once('close',r));f.fail();await f.g.verify();await closed;
  const body=await new Promise((resolve,reject)=>http.get(`http://127.0.0.1:${f.policy.corePort}`,res=>{let b='';res.on('data',c=>b+=c);res.on('end',()=>resolve(b));}).on('error',reject));
  assert.equal(body,'normal-network-canary');
});
test('stale lease after suspend fails closed without forwarding new connection',async t=>{
  const f=await fixture(t);await f.g.verify();f.g.lastOk=Date.now()-2000;
  assert.equal(await f.g.fresh(),false);assert.equal(await connectStatus(f.port),503);assert.equal(f.hits(),0);
});
test('late probe cannot reopen a manually blocked guard',async t=>{
  const f=await fixture(t);let release;f.g.probe=()=>new Promise(r=>release=r);
  const pending=f.g.verify();f.g.block('manual');release(f.policy.expectedIp);
  assert.equal(await pending,false);assert.equal(f.g.state,'BLOCKED');
});
test('invalid destination never reaches upstream',async t=>{
  const f=await fixture(t);await f.g.verify();assert.equal(await connectStatus(f.port,'example.com:80'),503);assert.equal(f.hits(),0);
});
test('dedicated upstream failure blocks; no direct fallback',async t=>{
  const f=await fixture(t);await f.g.verify();await new Promise(r=>f.upstream.close(r));
  await assert.rejects(tunnel(f.port,'example.com:443',500));assert.equal(f.g.state,'BLOCKED');assert.equal(f.hits(),0);
});
test('interface binding uses only listed names, in order, with IPv4',()=>{
  const nics={ Mihomo:[{family:'IPv4',internal:false}], WLAN:[{family:'IPv6',internal:false}],
    '以太网':[{family:'IPv4',internal:false}], Loopback:[{family:'IPv4',internal:true}] };
  assert.equal(pickInterface({interfaceName:['WLAN','以太网']},nics),'以太网');
  assert.equal(pickInterface({interfaceName:'以太网'},nics),'以太网');
  assert.throws(()=>pickInterface({interfaceName:'WLAN'},nics),/no IPv4 address: WLAN/);
  assert.throws(()=>pickInterface({interfaceName:['Loopback']},nics),/no IPv4/);
});
test('non-explicit verify (/verify) re-proves READY but never lifts a block',async t=>{
  const f=await fixture(t);assert.equal(await f.g.verify(),true);
  const before=f.g.lastOk;await delay(20);assert.equal(await f.g.verify(),true);assert.ok(f.g.lastOk>before);
  f.g.block('manual block');assert.equal(await f.g.verify(),false);assert.equal(f.g.state,'BLOCKED');
});
