'use strict';
// Keeps Claude Code itself pointing at the gate, however Claude is launched (Orca, terminal, desktop).
// Adds HTTPS_PROXY/HTTP_PROXY and the two guard hooks to ~/.claude/settings.json.
// Idempotent: rewrites only when the parsed content changes, and backs up the previous file first.
// Holds no node data; the only values it writes are the loopback proxy and this folder's script path.
const fs=require('node:fs'),path=require('node:path'),os=require('node:os'),crypto=require('node:crypto');
const dir=__dirname;
const PROXY='http://127.0.0.1:17899';
const HOOKS=['SessionStart','UserPromptSubmit'];
const hookFile=path.join(dir,'guard-hook.cjs').replace(/\\/g,'/');
const nodeFile=process.execPath.replace(/\\/g,'/');
function hookCommand(){return '"'+nodeFile+'" "'+hookFile+'"';}
function targets(argv){
  const file=argv.indexOf('--file');
  if(file>=0&&argv[file+1])return path.resolve(argv[file+1]);
  const base=process.env.CLAUDE_CONFIG_DIR||path.join(os.homedir(),'.claude');
  return path.join(base,'settings.json');
}
function read(file){
  if(!fs.existsSync(file))return {};
  let text;try{text=fs.readFileSync(file,'utf8');}catch(e){throw Error('无法读取 '+file+'：'+e.message);}
  try{const value=JSON.parse(text);if(!value||typeof value!=='object'||Array.isArray(value))throw Error('顶层不是对象');return value;}
  catch(e){throw Error(file+' 不是有效的 settings JSON，未做任何修改：'+e.message);}
}
// Returns the list of changes applied to `settings`.
function apply(settings){
  const changed=[];
  if(!settings.env||typeof settings.env!=='object'||Array.isArray(settings.env))settings.env={};
  for(const key of ['HTTPS_PROXY','HTTP_PROXY'])
    if(settings.env[key]!==PROXY){settings.env[key]=PROXY;changed.push('env.'+key);}
  if(!settings.hooks||typeof settings.hooks!=='object'||Array.isArray(settings.hooks))settings.hooks={};
  for(const event of HOOKS){
    if(!Array.isArray(settings.hooks[event])){settings.hooks[event]=[];changed.push('hooks.'+event);}
    const groups=settings.hooks[event];
    const present=groups.some(g=>g&&Array.isArray(g.hooks)&&g.hooks.some(h=>h&&typeof h.command==='string'&&h.command.includes('guard-hook.cjs')));
    if(!present){groups.push({hooks:[{type:'command',command:hookCommand(),timeout:60}]});if(!changed.includes('hooks.'+event))changed.push('hooks.'+event+'.guard');}
  }
  return changed;
}
function stamp(){
  const d=new Date(),p=n=>String(n).padStart(2,'0');
  return d.getFullYear()+p(d.getMonth()+1)+p(d.getDate())+'-'+p(d.getHours())+p(d.getMinutes())+p(d.getSeconds());
}
function run(argv){
  const file=targets(argv),quiet=argv.includes('--quiet');
  const before=read(file),after=JSON.parse(JSON.stringify(before));
  const changed=apply(after);
  if(JSON.stringify(before)===JSON.stringify(after)){
    if(!quiet)console.log('已是最新，无需修改：'+file);
    return {file,changed:false,backup:''};
  }
  let backup='';
  if(fs.existsSync(file)){backup=file+'.bak-'+stamp();fs.copyFileSync(file,backup);}
  fs.mkdirSync(path.dirname(file),{recursive:true});
  const temp=file+'.'+crypto.randomBytes(4).toString('hex')+'.tmp';
  fs.writeFileSync(temp,JSON.stringify(after,null,2)+'\n');
  fs.renameSync(temp,file);
  if(!quiet){
    console.log('已写入：'+file);
    console.log('新增：'+changed.join(', '));
    if(backup)console.log('原文件备份：'+backup);
    console.log('新会话生效；已经打开的 Claude 需要重开才会读取新设置。');
  }
  return {file,changed:true,backup,added:changed};
}
module.exports={apply,targets,run,PROXY,HOOKS,hookCommand};
if(require.main===module){
  try{run(process.argv.slice(2));}
  catch(e){console.error(e.message);process.exitCode=1;}
}
