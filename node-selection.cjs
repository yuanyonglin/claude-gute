'use strict';
// Only the dedicated Claude chain is configured. Never write to Clash profiles/API.
const fs=require('node:fs'),path=require('node:path'),net=require('node:net'),http=require('node:http'),crypto=require('node:crypto');
const {spawn,spawnSync}=require('node:child_process');
const yaml=require('./vendor/yaml');
const {getViaProxy,realProbe,pickInterface}=require('./guard-lib.cjs');
const dir=__dirname,run=path.join(dir,'guard-runtime');
const delay=ms=>new Promise(r=>setTimeout(r,ms));
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const read=f=>JSON.parse(fs.readFileSync(f,'utf8'));
const policyFile=path.join(dir,'guard-policy.json'),nodeFile=path.join(dir,'guard-node.json');
function atomic(file,value){const temp=file+'.'+crypto.randomBytes(6).toString('hex')+'.tmp';fs.writeFileSync(temp,JSON.stringify(value,null,2));fs.renameSync(temp,file);}
function catalog(file,base=path.join(process.env.APPDATA,'io.github.clash-verge-rev.clash-verge-rev')){
  let source='自选本地配置';
  if(!file){
    let manifest;try{manifest=yaml.parse(fs.readFileSync(path.join(base,'profiles.yaml'),'utf8'));}catch{throw Error('无法读取 Clash 配置索引，请手动选择 YAML 文件');}
    const item=manifest.items?.find(x=>x.uid===manifest.current);
    if(!item?.file)throw Error('Clash 当前订阅没有本地文件');
    file=path.resolve(base,'profiles',item.file);source=item.name||item.uid;
  }
  file=path.resolve(file);let bytes,doc;
  try{bytes=fs.readFileSync(file);if(bytes.length>16*1024*1024)throw Error();doc=yaml.parse(bytes.toString('utf8'));}catch{throw Error('无法解析本地 YAML 配置');}
  if(!Array.isArray(doc?.proxies))throw Error('配置不含内联 proxies 节点；请选择订阅 YAML，而非只有代理组的配置');
  const names=new Set();
  const nodes=doc.proxies.map(n=>{
    if(!n||typeof n.name!=='string'||names.has(n.name))throw Error('节点名称缺失或重名，不能安全确定目标');
    names.add(n.name);
    let supported=true,reason='';try{validateNode(n);}catch(e){supported=false;reason=e.message;}
    return {name:n.name,type:n.type,server:n.server,port:n.port,supported,reason};
  });
  return {file,source,digest:hash(bytes),nodes};
}
function validateNode(n){
  if(!['ss','vless','vmess','trojan'].includes(n.type))throw Error('该协议尚未支持隔离固定');
  if(net.isIP(n.server)!==4)throw Error('固定节点目前要求服务器为 IPv4 地址');
  if(!Number.isInteger(n.port)||n.port<1||n.port>65535)throw Error('无效端口');
  // Reject transport chaining or local binding overrides from imported profiles.
  if(['dialer-proxy','interface-name','routing-mark'].some(k=>k in n))throw Error('不允许节点自带代理链或网卡覆盖');
}
function selected(file,name,digest){
  const c=catalog(file);if(digest&&c.digest!==digest)throw Error('源配置已变化，请刷新节点并重新测试');
  const bytes=fs.readFileSync(c.file);if(hash(bytes)!==c.digest)throw Error('源配置读取期间发生变化，请重试');
  const doc=yaml.parse(bytes.toString('utf8'));
  const n=doc.proxies.find(n=>n.name===name);if(!n)throw Error('指定节点已不存在');validateNode(n);
  return {catalog:c,node:n};
}
function config(n,p,port){return {
  mode:'rule','log-level':'silent',ipv6:false,'allow-lan':false,'bind-address':'127.0.0.1',
  'mixed-port':port,'interface-name':pickInterface(p),'find-process-mode':'off',
  proxies:[{...n,name:'PINNED'}],rules:['MATCH,PINNED'],
  dns:{enable:true,ipv6:false,'enhanced-mode':'redir-host','use-hosts':false,'use-system-hosts':false,
    'default-nameserver':['1.1.1.1'],nameserver:['https://1.1.1.1/dns-query#PINNED','https://1.0.0.1/dns-query#PINNED'],
    'proxy-server-nameserver':['rcode://refused'],fallback:[]}
};}
async function freePort(){const s=net.createServer();await new Promise((r,j)=>{s.once('error',j);s.listen(0,'127.0.0.1',r);});const p=s.address().port;await new Promise(r=>s.close(r));return p;}
async function listening(port){return new Promise(r=>{const s=net.connect(port,'127.0.0.1');s.setTimeout(200);s.once('connect',()=>{s.destroy();r(true);});s.once('error',()=>r(false));s.once('timeout',()=>{s.destroy();r(false);});});}
async function isolatedProbe(n,p){
  fs.mkdirSync(run,{recursive:true});const temp=fs.mkdtempSync(path.join(run,'candidate-'));const port=await freePort();let core;
  try{
    const cfg=path.join(temp,'core.yaml');fs.writeFileSync(cfg,yaml.stringify(config(n,p,port)));
    const exe=path.join(dir,'mihomo-gate.exe'),args=['-d',temp,'-f',cfg];
    const check=spawnSync(exe,[...args,'-t'],{windowsHide:true,timeout:10000});
    if(check.status!==0)throw Error('候选节点未通过核心配置检查');
    core=spawn(exe,args,{windowsHide:true,stdio:'ignore'});let startError=false;core.on('error',()=>{startError=true;});
    for(let i=0;i<40;i++){if(startError||core.exitCode!==null)throw Error('候选核心启动失败');if(await listening(port))break;if(i===39)throw Error('候选核心启动超时');await delay(100);}
    const [raw,trace]=await Promise.all([getViaProxy(port,'https://api.ipify.org/',p.timeoutMs),getViaProxy(port,'https://www.cloudflare.com/cdn-cgi/trace',p.timeoutMs)]);
    const ip=raw.trim(),other=(trace.match(/^ip=(.+)$/m)||[])[1]?.trim(),country=(trace.match(/^loc=(.+)$/m)||[])[1]?.trim()||'未知';
    if(net.isIP(ip)!==4||ip!==other)throw Error('候选出口两路 IP 不一致或响应无效，拒绝保存');
    return {ip,country,observations:[{source:'ipify',ip},{source:'Cloudflare',ip:other}],checkedAt:new Date().toISOString()};
  }finally{
    if(core&&core.exitCode===null){core.kill();await Promise.race([new Promise(r=>core.once('exit',r)),delay(3000)]);}
    // This directory was created by this invocation and contains only its test core files.
    if(!core||core.exitCode!==null||core.signalCode!==null)fs.rmSync(temp,{recursive:true,force:true});
  }
}
// Expired test tickets can never be applied; drop them so guard-runtime does not grow. Unreadable ones are left for inspection.
function pruneTickets(now=Date.now()){
  for(const f of fs.readdirSync(run)){
    if(!/^selection-[a-f0-9]{48}\.json$/.test(f))continue;
    let t;try{t=read(path.join(run,f));}catch{continue;}
    if(!(t.expires>=now))fs.unlinkSync(path.join(run,f));
  }
}
async function prepare(request){
  const s=selected(request.file,request.name,request.digest),p=read(policyFile);
  const tested=await isolatedProbe(s.node,p);
  pruneTickets();
  const token=crypto.randomBytes(24).toString('hex');
  atomic(path.join(run,'selection-'+token+'.json'),{file:s.catalog.file,name:s.node.name,digest:s.catalog.digest,policyHash:hash(fs.readFileSync(policyFile)),...tested,expires:Date.now()+600000});
  return {ok:true,token,name:s.node.name,server:s.node.server,...tested};
}
function ticket(token){
  if(!/^[a-f0-9]{48}$/.test(token||''))throw Error('无效验证凭据');
  const t=read(path.join(run,'selection-'+token+'.json'));
  if(t.expires<Date.now())throw Error('验证已超过十分钟，请重新测试');
  if(hash(fs.readFileSync(policyFile))!==t.policyHash)throw Error('固定配置已变化，请重新测试');
  const s=selected(t.file,t.name,t.digest);return {...t,node:s.node};
}
function api(p,op){return new Promise((resolve,reject)=>{
  const req=http.request({host:'127.0.0.1',port:p.adminPort,path:'/'+op,method:op==='status'?'GET':'POST',headers:{Authorization:'Bearer '+fs.readFileSync(path.join(run,'admin-token'),'utf8').trim()},agent:false},res=>{
    let data='';res.on('data',x=>data+=x);res.on('end',()=>{try{if(res.statusCode!==200)throw Error('门卫拒绝操作');resolve(JSON.parse(data));}catch(e){reject(e);}});
  });req.setTimeout(2000,()=>req.destroy(Error('门卫控制超时')));req.on('error',reject);req.end();
});}
async function apply(request){
  if(request.confirm!==true)throw Error('必须明确确认保存');
  let fd;const lock=path.join(run,'node-selection.lock');
  try{fd=fs.openSync(lock,'wx');}catch{throw Error('已有节点保存操作，或上次操作异常；请检查后再试');}
  let changed=false,backup='';
  try{
    const t=ticket(request.token),p=read(policyFile);
    // A stale successful probe must never permit accepting a newly changed IP.
    const retest=await isolatedProbe(t.node,p);if(retest.ip!==t.ip||retest.country!==t.country)throw Error('候选出口发生变化，请重新测试并确认');
    ticket(request.token); // Recheck source and policy after network I/O.
    backup=path.join(dir,'backups','node-'+Date.now());fs.mkdirSync(backup,{recursive:true});
    for(const name of ['guard-policy.json','guard-node.json'])fs.copyFileSync(path.join(dir,name),path.join(backup,name));
    const marker=path.join(run,'configuration-pending.json');
    atomic(marker,{name:t.name,expectedIp:t.ip,backup,started:new Date().toISOString()});
    atomic(path.join(run,'blocked.json'),{reason:'fixed node change; explicit user resume required'});
    let live=false;try{await api(p,'block');live=true;}catch{if(await listening(p.adminPort))throw Error('无法确认旧门卫已阻断');}
    if(live)await api(p,'stop');
    for(let i=0;i<60;i++){if(!(await listening(p.adminPort))&&!(await listening(p.corePort))&&!(await listening(p.proxyPort)))break;if(i===59)throw Error('旧专用组件尚未退出，未修改固定配置');await delay(100);}
    // Journal denies resume even if the host crashes between these two atomic writes.
    const next={...p,label:t.name,expectedIp:t.ip,expectedServer:t.node.server,sourceFile:t.file,sourceNode:t.name,sourceDigest:t.digest,verifiedCountry:t.country};
    changed=true;atomic(nodeFile,t.node);atomic(policyFile,next);
    const child=spawn(process.execPath,[path.join(dir,'guard-daemon.cjs')],{detached:true,windowsHide:true,stdio:'ignore'});
    let failed=false;child.on('error',()=>{failed=true;});child.unref();
    let status;
    for(let i=0;i<80;i++){if(failed)throw Error('新门卫启动失败');try{status=await api(next,'status');}catch{}if(status?.state==='BLOCKED'&&status.expectedIp===t.ip&&await listening(next.corePort))break;if(i===79)throw Error('新门卫未能就绪');await delay(100);}
    await realProbe(next);
    if((await api(next,'status')).state!=='BLOCKED')throw Error('保存后门卫未保持锁定');
    fs.unlinkSync(marker);fs.unlinkSync(path.join(run,'selection-'+request.token+'.json'));
    return {ok:true,name:t.name,expectedIp:t.ip,country:t.country,state:'BLOCKED',backup,action:'已保存；未恢复任何 Claude 进程'};
  }catch(e){
    // Never clear the durable latch/journal on failure or silently fall back to another node.
    throw Error(e.message+(changed?'；配置事务未完成，保持锁定。备份：'+backup:'；未修改固定节点'));
  }finally{if(fd!==undefined){fs.closeSync(fd);fs.unlinkSync(lock);}}
}
module.exports={catalog,validateNode,selected,config,prepare,ticket,apply,isolatedProbe,pruneTickets};
if(require.main===module)(async()=>{
  let input='';for await(const chunk of process.stdin){input+=chunk;if(input.length>32768)throw Error('请求过大');}
  const r=input.trim()?JSON.parse(input):{};let result;
  if(process.argv[2]==='list')result={ok:true,...catalog(r.file)};
  else if(process.argv[2]==='test')result=await prepare(r);
  else if(process.argv[2]==='apply')result=await apply(r);
  else throw Error('未知操作');
  console.log(JSON.stringify(result));
})().catch(e=>{console.log(JSON.stringify({ok:false,error:e.message}));process.exitCode=1;});
