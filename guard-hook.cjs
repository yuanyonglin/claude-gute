'use strict';
const { spawnSync } = require('node:child_process');
const path = require('node:path');
let input='';process.stdin.on('data',c=>input+=c);
process.stdin.on('end',()=>{
  let event;try{event=JSON.parse(input).hook_event_name;}catch{console.error('Invalid hook input');process.exit(2);}
  if(event==='SessionStart'){
    const start=spawnSync('powershell.exe',['-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(__dirname,'start-guard.ps1')],{windowsHide:true,timeout:18000,encoding:'utf8'});
    if(start.status!==0){console.log(JSON.stringify({continue:false,stopReason:'Claude exit guard failed to start. Check guard-runtime logs.'}));process.exit(2);}
  }
  const check=spawnSync(process.execPath,[path.join(__dirname,'gate-control.cjs'),'check'],{windowsHide:true,timeout:14000,encoding:'utf8'});
  if(check.status!==0){
    console.error('Claude network BLOCKED. Inspect gate-control.cjs status; resolve the cause before explicit resume.');
    console.log(JSON.stringify({continue:false,stopReason:'Claude fixed-exit guard is not READY.'}));process.exit(2);
  }
  console.log('{}');
});
