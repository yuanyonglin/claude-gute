'use strict';
const fs=require('node:fs'),path=require('node:path'),net=require('node:net');
const {getViaProxy}=require('./guard-lib.cjs');
const policy=JSON.parse(fs.readFileSync(path.join(__dirname,'guard-policy.json')));
const started=Date.now();
const sources=[{name:'ipify',url:'https://api.ipify.org/',parse:x=>x.trim()},
 {name:'Cloudflare',url:'https://www.cloudflare.com/cdn-cgi/trace',parse:x=>(x.match(/^ip=(.+)$/m)||[])[1]?.trim()||''}];
(async()=>{
 const observations=await Promise.all(sources.map(async s=>{
  const t=Date.now();try{const ip=s.parse(await getViaProxy(policy.corePort,s.url,policy.timeoutMs));
   if(net.isIP(ip)!==4)throw Error('Invalid IPv4 response');
   return {source:s.name,ip,match:ip===policy.expectedIp,ms:Date.now()-t};
  }catch(e){return {source:s.name,ip:null,match:false,error:e.message,ms:Date.now()-t}}
 }));
 console.log(JSON.stringify({checkedAt:new Date().toISOString(),label:policy.label,expectedIp:policy.expectedIp,
  observations,ok:observations.every(x=>x.match),ms:Date.now()-started,action:'verification only; protection state unchanged'}));
 if(!observations.every(x=>x.match))process.exitCode=1;
})().catch(e=>{console.log(JSON.stringify({ok:false,error:e.message}));process.exitCode=1});
