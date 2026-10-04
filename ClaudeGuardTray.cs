using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public class Settings { public string mode = "confirm"; public string[] paths = new string[0]; public int adminPort=19099; }
public class Held { public int pid; public long start; public string path; }
public class Incident { public string reason; public List<Held> held = new List<Held>(); }

public sealed class ProcessGuard {
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr handle);
    public readonly List<Held> Held = new List<Held>();
    public readonly HashSet<string> Paths;
    public Action Persist = delegate {};
    public ProcessGuard(IEnumerable<string> paths) { Paths = new HashSet<string>(paths.Select(Normalize), StringComparer.OrdinalIgnoreCase); }
    // Store updates change only the version segment; publisher id and the rest of the path stay pinned.
    static readonly Regex StoreVersion = new Regex(@"\\WindowsApps\\Claude_[0-9.]+_x64__pzs8sxrjxfjjc\\",RegexOptions.IgnoreCase);
    public static string Normalize(string path) { return StoreVersion.Replace(Path.GetFullPath(path), @"\WindowsApps\Claude_*_x64__pzs8sxrjxfjjc\"); }
    public bool Matches(string path) { return Paths.Contains(Normalize(path)); }
    public IEnumerable<Process> Targets() {
        var names=new HashSet<string>(Paths.Select(Path.GetFileNameWithoutExtension),StringComparer.OrdinalIgnoreCase);
        foreach(Process p in Process.GetProcesses()) {
            bool match=false;try{match=names.Contains(p.ProcessName);}catch(InvalidOperationException){}
            if(match)yield return p;else p.Dispose();
        }
    }
    public string Protect(bool kill) {
        var errors = new List<string>(); int count=0;
        // A held record is not evidence that its process still exists. Never resume to prune.
        bool pruned=false;
        foreach(var h in Held.ToArray()) {
            try { using(var p=Process.GetProcessById(h.pid)) {
                if(p.HasExited || p.StartTime.ToUniversalTime().Ticks!=h.start || !String.Equals(p.MainModule.FileName,h.path,StringComparison.OrdinalIgnoreCase)) { Held.Remove(h);pruned=true; }
            }}
            catch(ArgumentException){Held.Remove(h);pruned=true;}
            catch(InvalidOperationException){Held.Remove(h);pruned=true;}
            catch(Exception e){errors.Add(h.pid+": 无法核对冻结记录："+e.Message);}
        }
        if(pruned)Persist();
        foreach(Process p in Targets()) using(p) {
            try {
                string file=p.MainModule.FileName;
                // Same name but unlisted path: report it instead of silently leaving it unprotected.
                if(!Matches(file)) { errors.Add(p.Id+": 同名进程不在保护名单 "+file); continue; }
                long birth=p.StartTime.ToUniversalTime().Ticks;
                if(kill) { p.Kill(); if(!p.WaitForExit(1000)) throw new Exception("结束未完成"); Held.RemoveAll(x=>x.pid==p.Id && x.start==birth); Persist(); count++; }
                else if(!Held.Any(x=>x.pid==p.Id && x.start==birth)) {
                    // Persist identity before suspend: if supervisor dies, recovery still knows the target.
                    var h=new Held{pid=p.Id,start=birth,path=file}; Held.Add(h); Persist();
                    int status=NtSuspendProcess(p.Handle);
                    if(status!=0) { Held.Remove(h); Persist(); throw new Exception("冻结失败 NTSTATUS="+status); }
                    count++;
                }
            } catch(InvalidOperationException) {} // Process exited during enumeration.
            catch(Exception e) { errors.Add(p.Id+": "+e.Message); }
        }
        return (kill?"本次已结束 ":"本轮新冻结 ")+count+" 个进程；当前冻结 "+Held.Count+" 个。"+(errors.Count>0?"\n未完成："+string.Join("; ",errors):"");
    }
    public string Release() {
        var errors=new List<string>();
        foreach(var h in Held.ToArray()) {
            try { using(var p=Process.GetProcessById(h.pid)) {
                if(p.StartTime.ToUniversalTime().Ticks==h.start && String.Equals(p.MainModule.FileName,h.path,StringComparison.OrdinalIgnoreCase)) {
                    int code=NtResumeProcess(p.Handle); if(code!=0) throw new Exception("恢复失败 NTSTATUS="+code);
                }
            } Held.Remove(h); Persist(); }
            catch(ArgumentException) { Held.Remove(h); Persist(); }
            catch(InvalidOperationException) { Held.Remove(h); Persist(); }
            catch(Exception e) { errors.Add(h.pid+": "+e.Message); }
        }
        return string.Join("; ",errors);
    }
}

public sealed class Tray : Form {
    readonly string dir, runtime;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly HttpClient client;
    readonly SemaphoreSlim serial=new SemaphoreSlim(1,1);
    Settings settings;
    ProcessGuard processes;
    Incident incident;
    NotifyIcon icon;
    System.Threading.Timer timer;
    ToolStripMenuItem confirm, automatic, state;
    Dashboard dashboard;
    Icon brandIcon;
    string nodeLabel="未知节点", expectedIp="未配置", lastVerified="";
    EventWaitHandle openSignal;
    RegisteredWaitHandle openWait;
    EventWaitHandle desktopSignal;
    RegisteredWaitHandle desktopWait;
    string lastAction="尚未操作。继续使用请选择「复检并恢复」，不是「结束进程」。";
    bool recovering=false;
    string status="检查中", lastError="", result="";
    bool armed=false, closing=false;
    int busy=0, tick=0;
    DateTime started=DateTime.UtcNow;
    public Tray(string directory) {
        dir=directory; runtime=Path.Combine(dir,"guard-runtime"); Directory.CreateDirectory(runtime);
        settings=json.Deserialize<Settings>(File.ReadAllText(Path.Combine(dir,"tray-settings.json")));
        ReadPolicy();
        if(settings.mode!="confirm" && settings.mode!="auto") throw new Exception("Invalid termination mode");
        processes=new ProcessGuard(settings.paths);
        if(File.Exists(IncidentFile)) { incident=json.Deserialize<Incident>(File.ReadAllText(IncidentFile)); processes.Held.AddRange(incident.held); }
        processes.Persist=SaveIncident;
        client=new HttpClient(new HttpClientHandler{UseProxy=false}); client.Timeout=TimeSpan.FromMilliseconds(1300);
        Text="Claude 网络保护"; ShowInTaskbar=false; WindowState=FormWindowState.Minimized;
        var menu=new ContextMenuStrip();
        state=new ToolStripMenuItem("检查中"); state.Enabled=false; menu.Items.Add(state);
        menu.Items.Add("打开主界面",null,(s,e)=>OpenDashboard());
        confirm=new ToolStripMenuItem("需要确认：先冻结，确认后结束",null,(s,e)=>ChangeMode("confirm"));
        automatic=new ToolStripMenuItem("不需要确认：自动结束全部 Claude",null,(s,e)=>ChangeMode("auto"));
        menu.Items.Add(confirm); menu.Items.Add(automatic); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("显示当前告警 / 状态",null,(s,e)=>ShowStatus());
        menu.Items.Add("复检并恢复（不重启已结束进程）",null,async(s,e)=>await Recover());
        menu.Items.Add("预览弹窗（不影响进程）",null,(s,e)=>Preview());
        menu.Items.Add("退出托盘（不会解除阻断或冻结）",null,(s,e)=>ExitTray());
        brandIcon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        icon=new NotifyIcon{Text="Claude 网络保护",Icon=brandIcon,Visible=true,ContextMenuStrip=menu};
        icon.DoubleClick+=(s,e)=>OpenDashboard(); UpdateMode();
        openSignal=new EventWaitHandle(false,EventResetMode.AutoReset,Program.SignalName("Open"));
        openWait=ThreadPool.RegisterWaitForSingleObject(openSignal,(s,t)=>UI(()=>OpenDashboard()),null,Timeout.Infinite,false);
        desktopSignal=new EventWaitHandle(false,EventResetMode.AutoReset,Program.SignalName("Desktop"));
        desktopWait=ThreadPool.RegisterWaitForSingleObject(desktopSignal,(s,t)=>UI(()=>Launch(true)),null,Timeout.Infinite,false);
        Load+=(s,e)=>Hide();
        Shown+=(s,e)=>Hide();
        NetworkChange.NetworkAvailabilityChanged+=NetworkChanged;
        NetworkChange.NetworkAddressChanged+=AddressChanged;
        timer=new System.Threading.Timer(_=>Poll(),null,1000,1000);
    }
    // Default install first, then PATH; fail loudly instead of guessing.
    static string NodeExe {get{
        string d=@"C:\Program Files\nodejs\node.exe";if(File.Exists(d))return d;
        foreach(var p in (Environment.GetEnvironmentVariable("PATH")??"").Split(';')){try{var f=Path.Combine(p.Trim().Trim('"'),"node.exe");if(p.Trim()!=""&&File.Exists(f))return f;}catch(ArgumentException){}}
        throw new Exception("找不到 node.exe：请安装 Node.js 或把它加入 PATH");}}
    string IncidentFile {get{return Path.Combine(runtime,"tray-incident.json");}}
    int staleSeconds=30;
    void ReadPolicy(){var f=Path.Combine(dir,"guard-policy.json");if(File.Exists(f)){var p=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(f));nodeLabel=Convert.ToString(p["label"]);expectedIp=Convert.ToString(p["expectedIp"]);
        int interval=p.ContainsKey("intervalMs")?Convert.ToInt32(p["intervalMs"]):5000,timeout=p.ContainsKey("timeoutMs")?Convert.ToInt32(p["timeoutMs"]):12000;
        staleSeconds=(interval+3*(timeout+1000)+5000)/1000;}}
    async Task<Dictionary<string,object>> NodeCommand(string action,object request){
        string input=json.Serialize(request);
        string output=await Task.Run(()=>{using(var p=new Process{StartInfo=new ProcessStartInfo(NodeExe,"\""+Path.Combine(dir,"node-selection.cjs")+"\" "+action){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8}}){
            p.Start();using(var writer=new StreamWriter(p.StandardInput.BaseStream,new System.Text.UTF8Encoding(false))){writer.Write(input);}var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
            if(!p.WaitForExit(60000)){p.Kill();throw new Exception("节点操作超时，请检查锁定状态和日志；不要手动恢复 Claude。");}Task.WaitAll(stdout,stderr);
            if(String.IsNullOrWhiteSpace(stdout.Result))throw new Exception("节点操作未返回结果");return stdout.Result;
        }});
        var r=json.Deserialize<Dictionary<string,object>>(output);if(!Convert.ToBoolean(r["ok"]))throw new Exception(Convert.ToString(r["error"]));return r;
    }
    bool choosingNode;
    void ChooseNode(){
        if(choosingNode)return;choosingNode=true;dashboard.Verify.Enabled=dashboard.Resume.Enabled=dashboard.SelectNode.Enabled=false;
        try{using(var picker=new NodePicker(brandIcon,nodeLabel,NodeCommand,async token=>{
            await serial.WaitAsync();try{await Fail("用户更换固定节点，等待复检恢复");}finally{serial.Release();}
            // Keep the process watchdog polling while candidate network I/O is in flight.
            try{return await NodeCommand("apply",new{token=token,confirm=true});}
            finally{ReadPolicy();lastVerified="";dashboard.Observed.Text="配置已重新读取，请验证出口";RefreshDashboard("BLOCKED",incident==null?"等待复检恢复":incident.reason);}
        }))picker.ShowDialog(dashboard);}
        finally{choosingNode=false;dashboard.Verify.Enabled=dashboard.Resume.Enabled=dashboard.SelectNode.Enabled=true;}
    }
    void Atomic(string file,string text) { string tmp=file+".tmp"; File.WriteAllText(tmp,text); if(File.Exists(file)) File.Replace(tmp,file,null); else File.Move(tmp,file); }
    void SaveIncident() { if(incident!=null) { incident.held=processes.Held; Atomic(IncidentFile,json.Serialize(incident)); } }
    void Log(string text) { var f=Path.Combine(runtime,"tray.log"); if(File.Exists(f)&&new FileInfo(f).Length>1048576) File.Copy(f,f+".previous",true); if(File.Exists(f)&&new FileInfo(f).Length>1048576)File.WriteAllText(f,""); File.AppendAllText(f,DateTime.Now.ToString("s")+" "+text+Environment.NewLine); }
    void UI(Action action) { if(!closing && IsHandleCreated) BeginInvoke(action); }
    async Task<Dictionary<string,object>> Api(string op,int timeout=1300) {
        var token=File.ReadAllText(Path.Combine(runtime,"admin-token")).Trim();
        using(var request=new HttpRequestMessage(op=="status"?HttpMethod.Get:HttpMethod.Post,"http://127.0.0.1:"+settings.adminPort+"/"+op)) {
            request.Headers.Add("Authorization","Bearer "+token);
            using(var response=await client.SendAsync(request)) {
                string text=await response.Content.ReadAsStringAsync();
                if(!response.IsSuccessStatusCode) throw new Exception("门卫拒绝请求 HTTP "+(int)response.StatusCode);
                return json.Deserialize<Dictionary<string,object>>(text);
            }
        }
    }
    async Task BlockApi() { try{await Api("block");}catch(Exception e){Log("无法联系门卫阻断："+e.Message);} }
    void NetworkChanged(object s,NetworkAvailabilityEventArgs e) { if(!e.IsAvailable) Fault("Windows 报告网络断开"); }
    int rechecking=0;
    // Address changes (DHCP renew, virtual adapters) are common; recheck the exit once before freezing anything.
    void AddressChanged(object s,EventArgs e) {
        if(!armed||incident!=null||Interlocked.Exchange(ref rechecking,1)!=0)return;
        Task.Run(async()=>{
            try{Log("网络地址变化，自动复检");if(await FreshVerify())Log("自动复检通过");else Fault("网络地址变化，自动复检未通过");}
            catch(Exception ex){Fault("网络地址变化，自动复检失败："+ex.Message);}
            finally{Interlocked.Exchange(ref rechecking,0);}
        });
    }
    // True only for a fresh READY proof of the configured exit. An older daemon without /verify returns 404: treated as failure.
    async Task<bool> FreshVerify(){
        using(var c=new HttpClient(new HttpClientHandler{UseProxy=false})){
            c.Timeout=TimeSpan.FromSeconds(45);c.DefaultRequestHeaders.Add("Authorization","Bearer "+File.ReadAllText(Path.Combine(runtime,"admin-token")).Trim());
            using(var r=await c.PostAsync("http://127.0.0.1:"+settings.adminPort+"/verify",null)){
                if(!r.IsSuccessStatusCode)return false;
                var v=json.Deserialize<Dictionary<string,object>>(await r.Content.ReadAsStringAsync());DateTime at;
                if(!v.ContainsKey("state")||Convert.ToString(v["state"])!="READY"||!v.ContainsKey("expectedIp")||Convert.ToString(v["expectedIp"])!=expectedIp)return false;
                if(!v.ContainsKey("lastVerified")||!DateTime.TryParse(Convert.ToString(v["lastVerified"]),out at))return false;
                double age=(DateTime.UtcNow-at.ToUniversalTime()).TotalSeconds;return age>=0&&age<=10;
            }
        }
    }
    void Fault(string reason) { Task.Run(async()=>{await serial.WaitAsync();try{await Fail(reason);}catch(Exception e){Report(e);}finally{serial.Release();}}); }
    void Report(Exception e) { lastError=e.Message; try{Log("ERROR "+e);}catch{} UI(()=>{state.Text="保护错误："+lastError;icon.ShowBalloonTip(5000,"Claude 保护执行异常",lastError,ToolTipIcon.Error);}); }
    string ReasonText(string reason){
        if(reason=="dedicated upstream connection failed")return "专用代理连接失败，保护保持锁定";
        if(reason=="manual block")return "已暂停，等待复检恢复";
        if(reason=="fixed exit verified")return "固定出口验证通过，正在持续监测";
        if(reason=="dedicated core exited")return "专用代理核心已退出";
        const string nic="configured network interface has no IPv4 address: ";
        if(reason!=null&&reason.StartsWith(nic))return "网卡不可用（"+reason.Substring(nic.Length)+"）：检查网络，或在 guard-policy.json 的 interfaceName 中加入备用网卡后重启门卫";
        return reason;
    }
    DateTime eventsRead=DateTime.MinValue;
    void RefreshEvents(){
        if(dashboard==null||dashboard.IsDisposed||!dashboard.Visible||(DateTime.UtcNow-eventsRead).TotalSeconds<3)return;
        eventsRead=DateTime.UtcNow;
        var rows=new List<string[]>();string lastConnect=null;var file=Path.Combine(runtime,"events.jsonl");
        if(File.Exists(file)){
            string tail;
            // Share write and delete: the daemon appends and rotates this file, and a failed log write there blocks the guard.
            using(var fs=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                long start=Math.Max(0,fs.Length-32768);fs.Seek(start,SeekOrigin.Begin);
                using(var reader=new StreamReader(fs,System.Text.Encoding.UTF8))tail=reader.ReadToEnd();
                if(start>0){int nl=tail.IndexOf('\n');tail=nl<0?"":tail.Substring(nl+1);}
            }
            foreach(var line in tail.Split('\n').Reverse()){
                if(rows.Count>=6)break;if(line.Trim()=="")continue;
                Dictionary<string,object> e;
                try{e=json.Deserialize<Dictionary<string,object>>(line);}catch(ArgumentException){continue;} // a line still being appended
                DateTime at;if(e==null||!e.ContainsKey("event")||!e.ContainsKey("time")||!DateTime.TryParse(Convert.ToString(e["time"]),out at))continue;
                string time=at.ToLocalTime().ToString("HH:mm"),kind=Convert.ToString(e["event"]);
                Func<string,string> field=k=>e.ContainsKey(k)?Convert.ToString(e[k]):"";
                if(kind=="CONNECT"){if(lastConnect==null)lastConnect=time+" "+field("authority");continue;}
                if(kind=="BLOCKED")rows.Add(new[]{time,"锁定",ReasonText(field("reason")),"bad"});
                else if(kind=="READY")rows.Add(new[]{time,"正常","固定出口验证通过","good"});
                else if(kind=="MANUAL_RESUME")rows.Add(new[]{time,"恢复","复检通过，手动恢复","good"});
                else if(kind=="STARTING")rows.Add(new[]{time,"验证","正在验证出口","muted"});
                else if(kind=="START")rows.Add(new[]{time,"启动","门卫启动"+(field("interface")==""?"":" · 网卡 "+field("interface")),"muted"});
                else if(kind=="PROBE_RETRY")rows.Add(new[]{time,"重试","探测重试 "+field("attempt")+"/3 · "+field("error"),"muted"});
                else if(kind=="FATAL")rows.Add(new[]{time,"故障",field("reason"),"bad"});
                else rows.Add(new[]{time,kind,"","muted"});
            }
        }
        dashboard.SetEvents(rows,lastConnect);
    }
    void RefreshDashboard(string phase,string reason){if(dashboard!=null&&!dashboard.IsDisposed){dashboard.SetState(phase,ReasonText(reason),lastVerified,processes.Held.Count,nodeLabel,expectedIp);try{RefreshEvents();}catch(IOException e){Log("读取事件失败："+e.Message);}dashboard.Updating=true;dashboard.Confirm.Checked=settings.mode=="confirm";dashboard.Auto.Checked=settings.mode=="auto";dashboard.Updating=false;}}
    public void OpenDashboard(){
        bool firstOpen=dashboard==null||dashboard.IsDisposed;
        if(dashboard==null||dashboard.IsDisposed){
            dashboard=new Dashboard(brandIcon);
            dashboard.Confirm.CheckedChanged+=(s,e)=>{if(!dashboard.Updating&&dashboard.Confirm.Checked)ChangeMode("confirm");};
            dashboard.Auto.CheckedChanged+=(s,e)=>{if(!dashboard.Updating&&dashboard.Auto.Checked)ChangeMode("auto");};
            dashboard.Verify.Click+=async(s,e)=>await InspectExit();
            dashboard.SelectNode.Click+=(s,e)=>ChooseNode();
            dashboard.Resume.Click+=async(s,e)=>await Recover();
            dashboard.Alert.Click+=(s,e)=>ShowStatus();
            dashboard.IncidentRecover.Click+=async(s,e)=>await Recover();
            dashboard.IncidentEnd.Click+=async(s,e)=>await EndProcesses();
            dashboard.IncidentDismiss.Click+=(s,e)=>{Log("用户关闭提示；仍保持冻结与阻断");dashboard.HideIncident();};
            dashboard.Logs.Click+=(s,e)=>{try{Process.Start(new ProcessStartInfo("explorer.exe","\""+runtime+"\""){UseShellExecute=true});}catch(Exception ex){Report(ex);}};
            dashboard.Code.Click+=(s,e)=>Launch(false);dashboard.Desktop.Click+=(s,e)=>Launch(true);
        }
        RefreshDashboard(incident!=null?"BLOCKED":armed?"READY":"STARTING",incident!=null?incident.reason:status);
        dashboard.Show();dashboard.WindowState=FormWindowState.Normal;dashboard.Activate();
        if(firstOpen)dashboard.Verify.PerformClick();
    }
    async Task InspectExit(){
        dashboard.SelectNode.Enabled=false;
        dashboard.Verify.Enabled=false;dashboard.Inspection.Text="正在通过专用代理查询 ipify 和 Cloudflare…";
        var executable=Path.Combine(dir,"guard-inspect.cjs");
        string output=await Task.Run(()=>{
            using(var p=new Process{StartInfo=new ProcessStartInfo(NodeExe,"\""+executable+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}}){
                p.Start();var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
                if(!p.WaitForExit(30000)){p.Kill();throw new Exception("出口验证超时");}Task.WaitAll(stdout,stderr);
                if(String.IsNullOrWhiteSpace(stdout.Result))throw new Exception(stderr.Result);return stdout.Result;
            }
        }).ContinueWith(t=>{if(t.IsFaulted)return "ERROR: "+t.Exception.GetBaseException().Message;return t.Result;});
        try{
            if(output.StartsWith("ERROR:"))throw new Exception(output);
            var r=json.Deserialize<Dictionary<string,object>>(output);var rows=(System.Collections.IEnumerable)r["observations"];var table=new List<string[]>();var ips=new List<string>();
            foreach(Dictionary<string,object> row in rows){var ip=Convert.ToString(row["ip"]);bool match=Convert.ToBoolean(row["match"]);if(ip!="")ips.Add(ip);
                table.Add(new[]{Convert.ToString(row["source"]),ip!=""?ip:Convert.ToString(row["error"]),ip==""?"失败":match?"一致":"不一致",match?"ok":"bad"});}
            bool ok=Convert.ToBoolean(r["ok"]);dashboard.SetObservations(table);
            dashboard.Observed.Text=ok?"两路一致 · "+ips.FirstOrDefault():ips.Count>0?"检测不匹配 / 未全部通过":"本次未能获取 IP";
            dashboard.Observed.ForeColor=ok?Theme.GoodInk:Theme.BadInk;
            dashboard.Inspection.Text=DateTime.Parse(Convert.ToString(r["checkedAt"])).ToLocalTime().ToString("HH:mm:ss")+" 实测 · "+(ok?"没有改变锁定状态":"保护状态保持不变");
            Log("UI_VERIFY "+output.Trim());
        }catch(Exception e){dashboard.Observed.Text="检测失败";dashboard.Observed.ForeColor=Theme.BadInk;dashboard.Inspection.Text=e.Message;}
        finally{dashboard.Verify.Enabled=true;dashboard.SelectNode.Enabled=!choosingNode;}
    }
    void Launch(bool desktop){
        Task.Run(()=>{try{var start=Process.Start(new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy Bypass -File \""+Path.Combine(dir,"start-guard.ps1")+"\""){UseShellExecute=false,CreateNoWindow=true});start.WaitForExit();if(start.ExitCode!=0)throw new Exception("保护启动失败");
            UI(()=>{try{if(incident!=null)throw new Exception("当前已锁定，请先复检并恢复。");
            if(desktop)Task.Run(()=>{using(var p=new Process{StartInfo=new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy Bypass -File \""+Path.Combine(dir,"launch-desktop.ps1")+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}}){p.Start();var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();p.WaitForExit();Task.WaitAll(output,error);if(p.ExitCode!=0)UI(()=>MessageBox.Show(error.Result+output.Result,"桌面启动失败"));}});
            else Process.Start(new ProcessStartInfo(Path.Combine(dir,"claude-safe.cmd")){UseShellExecute=true});}catch(Exception e){MessageBox.Show(e.Message,"尚未启动");}});
        }catch(Exception e){UI(()=>MessageBox.Show(e.Message,"尚未启动"));}});
    }
    public void RequestDesktop(){Launch(true);}
    public async Task TestNodePicker(string image){
        try{
            if(settings.paths.Length!=0||settings.adminPort==19099)throw new Exception("Node UI test requires isolated fixture with no process targets");
            using(var picker=new NodePicker(brandIcon,nodeLabel,NodeCommand,token=>NodeCommand("apply",new{token=token,confirm=true}))){picker.Show();await picker.TestWorkflow(image);picker.Close();}
        }catch(Exception e){File.WriteAllText(image+".txt",e.ToString());Environment.ExitCode=1;}
        finally{closing=true;timer.Dispose();icon.Dispose();Application.Exit();}
    }
    public async Task TestDashboard(string image){
        try{
            OpenDashboard();while(!dashboard.Verify.Enabled)await Task.Delay(100);
            if(dashboard.Observed.Text!=expectedIp)throw new Exception("Dashboard verification did not display expected IP: "+dashboard.Inspection.Text);
            dashboard.Refresh();using(var b=new Bitmap(dashboard.Width,dashboard.Height)){dashboard.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(image,System.Drawing.Imaging.ImageFormat.Png);}
            File.WriteAllText(image+".txt","PASS dashboard opens\nPASS real two-source verification displays expected IP\nPASS verification leaves protection unchanged\n"+dashboard.Inspection.Text);
        }catch(Exception e){File.WriteAllText(image+".txt",e.ToString());Environment.ExitCode=1;}
        finally{closing=true;timer.Dispose();icon.Dispose();Application.Exit();}
    }
    async Task Fail(string reason) {
        if(incident==null) { incident=new Incident{reason=reason};lastAction="尚未操作。继续使用请选择「复检并恢复」，不是「结束进程」。"; SaveIncident(); Log("故障 "+reason); }
        // Suspend/kill first if the gate is hung: do not wait for a network control timeout.
        result=processes.Protect(settings.mode=="auto");
        await BlockApi();
        status="已锁定："+incident.reason;
        UI(()=>{state.Text=status; icon.Icon=SystemIcons.Error; RefreshDashboard("BLOCKED",incident.reason);ShowIncident();});
    }
    async void Poll() {
        if(Interlocked.Exchange(ref busy,1)!=0)return;
        await serial.WaitAsync();
        try {
            if(incident!=null) {
                if(++tick%2==0) result=processes.Protect(settings.mode=="auto");
                try{var live=await Api("status");lastVerified=Convert.ToString(live["lastVerified"]);}catch(Exception ex){lastError=ex.Message;}
                await BlockApi();
                UI(()=>{state.Text="已锁定："+incident.reason;RefreshDashboard("BLOCKED",incident.reason);ShowIncident();}); return;
            }
            var s=await Api("status");
            var phase=Convert.ToString(s["state"]);
            lastVerified=Convert.ToString(s["lastVerified"]);
            if(phase=="READY") {
                DateTime last=DateTime.Parse(Convert.ToString(s["lastVerified"])).ToUniversalTime();
                double age=(DateTime.UtcNow-last).TotalSeconds;
                if(age<0||age>staleSeconds) {await Fail("门卫校验过期 / 无响应");return;}
                armed=true;status="正常：固定出口已验证";UI(()=>{state.Text=status;icon.Icon=brandIcon;RefreshDashboard("READY",Convert.ToString(s["reason"]));});
            } else if(phase=="STARTING" && !armed && (DateTime.UtcNow-started).TotalSeconds<15) { }
            else await Fail(Convert.ToString(s["reason"]));
        } catch(Exception e) {
            if(armed || (DateTime.UtcNow-started).TotalSeconds>15) Fault("门卫不可用："+e.Message);
        } finally {serial.Release();Interlocked.Exchange(ref busy,0);}
    }
    bool shownIncident=false;
    void ShowIncident() {
        if(incident==null)return;
        if(dashboard!=null&&!dashboard.IsDisposed&&dashboard.IncidentVisible){dashboard.IncidentText.Text=AlertText();return;}
        if(shownIncident)return; shownIncident=true;
        OpenDashboard();
        dashboard.IncidentAction.Text=lastAction;
        dashboard.ShowIncident(AlertText());
    }
    void ActionResult(string text){lastAction=text;if(dashboard!=null&&!dashboard.IsDisposed)dashboard.IncidentAction.Text=text;}
    async Task EndProcesses(){
        dashboard.IncidentEnd.Enabled=false;ActionResult("正在结束已识别的 Claude 进程，请稍候…");
        await serial.WaitAsync();try{
            result=processes.Protect(true);Log(result);ActionResult(DateTime.Now.ToString("HH:mm:ss")+" "+result+"\n保护仍锁定；重新使用前需复检恢复。");
            RefreshDashboard("BLOCKED",incident==null?status:incident.reason);if(dashboard!=null&&!dashboard.IsDisposed)dashboard.IncidentText.Text=AlertText();
        }catch(Exception ex){ActionResult("结束未完成："+ex.Message);Report(ex);}finally{serial.Release();if(dashboard!=null&&!dashboard.IsDisposed)dashboard.IncidentEnd.Enabled=true;}
    }
    string AlertText(){return "新启动的 Claude 也会被暂停，终端可能显示空白。「复检并恢复」通过出口验证后才恢复进程；「结束」只关闭进程，不会解锁。"+(result==""?"":"\n"+result);}
    void ShowStatus(){if(incident!=null){shownIncident=false;ShowIncident();}else MessageBox.Show(status+"\n模式："+settings.mode+"\n"+lastError,"Claude 网络保护");}
    void UpdateMode(){confirm.Checked=settings.mode=="confirm";automatic.Checked=settings.mode=="auto";RefreshDashboard(incident!=null?"BLOCKED":armed?"READY":"STARTING",incident!=null?incident.reason:status);}
    async void ChangeMode(string mode) {
        if(mode==settings.mode)return;
        if(mode=="auto"&&MessageBox.Show("启用后，检测到异常会强制结束已识别的全部 Claude Code 和桌面进程，可能中断任务。当前已锁定时也会立即结束。确定？","开启自动结束",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes){UpdateMode();return;}
        await serial.WaitAsync();try{settings.mode=mode;Atomic(Path.Combine(dir,"tray-settings.json"),json.Serialize(settings));UpdateMode();if(incident!=null)result=processes.Protect(mode=="auto");}catch(Exception ex){Report(ex);}finally{serial.Release();}
    }
    async Task Recover(bool askUser=true) {
        if(recovering)return;
        if(choosingNode){MessageBox.Show("请先关闭节点选择窗口，再复检恢复。","节点配置中");return;}
        ReadPolicy();
        if(askUser && MessageBox.Show(dashboard,"将使用固定节点："+nodeLabel+"\n预期 IP："+expectedIp+"\n\n验证通过后恢复本程序冻结的进程，它们可能立即继续请求。\n已结束的进程不会自动重启。请确认节点符合你的要求。","确认固定节点并恢复",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        recovering=true;if(dashboard!=null&&!dashboard.IsDisposed){dashboard.Resume.Enabled=false;dashboard.IncidentRecover.Enabled=false;}
        ActionResult("正在重新验证固定出口，尚未恢复进程…");
        await serial.WaitAsync();
        try {
          Exception recoveryError=null;
          try {
            // Resume needs the probe deadline, longer than normal status calls.
            using(var c=new HttpClient(new HttpClientHandler{UseProxy=false})) {
                c.Timeout=TimeSpan.FromSeconds(45);c.DefaultRequestHeaders.Add("Authorization","Bearer "+File.ReadAllText(Path.Combine(runtime,"admin-token")).Trim());
                using(var r=await c.PostAsync("http://127.0.0.1:"+settings.adminPort+"/resume",null)){
                    r.EnsureSuccessStatusCode();
                    var verified=json.Deserialize<Dictionary<string,object>>(await r.Content.ReadAsStringAsync());
                    DateTime at;double age;
                    if(!verified.ContainsKey("state")||Convert.ToString(verified["state"])!="READY"||!verified.ContainsKey("expectedIp")||Convert.ToString(verified["expectedIp"])!=expectedIp||!verified.ContainsKey("lastVerified")||!DateTime.TryParse(Convert.ToString(verified["lastVerified"]),out at)||(age=(DateTime.UtcNow-at.ToUniversalTime()).TotalSeconds)<0||age>10)
                        throw new Exception("门卫没有返回当前固定出口的新鲜 READY 证明，保持冻结");
                    lastVerified=Convert.ToString(verified["lastVerified"]);
                }
            }
            string errors=processes.Release();if(errors!="")throw new Exception(errors);
            File.Delete(IncidentFile);incident=null;shownIncident=false;armed=true;status="复检通过";ActionResult("复检通过，已恢复现有冻结进程；已结束进程不会重启。");if(dashboard!=null&&!dashboard.IsDisposed)dashboard.HideIncident();RefreshDashboard("READY","fixed exit verified");Log("用户复检恢复");
          }catch(Exception e){recoveryError=e;}
          if(recoveryError!=null){
            var e=recoveryError;
            await Fail("复检恢复未完成："+e.Message);Report(e);ActionResult("未恢复："+e.Message+"。已保持阻断。");
            if(askUser)MessageBox.Show("未恢复："+e.Message,"复检失败");
          }
        }finally{serial.Release();recovering=false;if(dashboard!=null&&!dashboard.IsDisposed){dashboard.Resume.Enabled=!choosingNode;dashboard.IncidentRecover.Enabled=true;}}
    }
    public async Task TestRecovery(string output){
        Process child=null;var checks=new List<string>();
        try{
            if(settings.adminPort==19099||settings.paths.Length!=1||Path.GetDirectoryName(settings.paths[0])!=dir.TrimEnd(Path.DirectorySeparatorChar)||Path.GetFileName(settings.paths[0])!="GuardTestChild.exe")throw new Exception("Recovery test requires an isolated exact-path child");
            timer.Dispose();await serial.WaitAsync();serial.Release();
            string heartbeat=Path.Combine(dir,"heartbeat");
            Func<Process> start=()=>Process.Start(new ProcessStartInfo(settings.paths[0],"--test-child \""+heartbeat+"\""){UseShellExecute=false,CreateNoWindow=true});
            child=start();await Task.Delay(300);await Fail("ISOLATED RECOVERY TEST");ShowIncident();
            if(!dashboard.IncidentVisible)throw new Exception("Incident card did not appear in dashboard");
            long size=new FileInfo(heartbeat).Length;await Task.Delay(200);if(new FileInfo(heartbeat).Length!=size)throw new Exception("Child was not frozen");
            checks.Add("PASS fault freezes isolated child and shows incident inside dashboard");
            dashboard.IncidentEnd.PerformClick();for(int i=0;i<60&&!dashboard.IncidentEnd.Enabled;i++)await Task.Delay(50);
            if(!child.HasExited||processes.Held.Count!=0||!lastAction.Contains("本次已结束 1"))throw new Exception("End button did not report exact result");
            checks.Add("PASS actual end button terminates child and clears count");
            child=start();await Task.Delay(300);await Fail("ISOLATED RECOVERY TEST");ShowIncident();
            if(!dashboard.IncidentAction.Text.Contains("本次已结束 1"))throw new Exception("Background status erased action feedback");
            checks.Add("PASS background refresh preserves last button result");
            File.WriteAllText(Path.Combine(dir,"response-mode"),"bad");await Recover(false);
            size=new FileInfo(heartbeat).Length;await Task.Delay(200);
            if(incident==null||processes.Held.Count!=1||new FileInfo(heartbeat).Length!=size)throw new Exception("Invalid resume proof released child");
            checks.Add("PASS HTTP 200 with BLOCKED is rejected without releasing child");
            File.WriteAllText(Path.Combine(dir,"response-mode"),"good");await Recover(false);await Task.Delay(200);
            if(incident!=null||processes.Held.Count!=0||File.Exists(IncidentFile)||new FileInfo(heartbeat).Length<=size)throw new Exception("Verified recovery failed");
            checks.Add("PASS fresh matching READY resumes child and clears incident");
            await Fail("ISOLATED RECOVERY TEST");ShowIncident();
            dashboard.Refresh();using(var bitmap=new Bitmap(dashboard.Width,dashboard.Height)){dashboard.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));bitmap.Save(output+".png",System.Drawing.Imaging.ImageFormat.Png);}
            dashboard.IncidentDismiss.PerformClick();
            size=new FileInfo(heartbeat).Length;await Task.Delay(200);ShowIncident();
            if(dashboard.IncidentVisible||incident==null||new FileInfo(heartbeat).Length!=size)throw new Exception("Dismiss button did not retain freeze and hide incident card");
            checks.Add("PASS actual dismiss button hides incident card while retaining freeze");
        }catch(Exception e){checks.Add("FAIL "+e);Environment.ExitCode=1;}
        finally{if(child!=null){try{if(!child.HasExited){child.Kill();child.WaitForExit(2000);}}catch(Exception e){checks.Add("CLEANUP ERROR "+e);Environment.ExitCode=1;}}File.WriteAllLines(output,checks);closing=true;timer.Dispose();icon.Dispose();Application.Exit();}
    }
    void Preview(){using(var f=new Form{Text="测试预览 · 不操作真实进程",Size=new Size(520,230),StartPosition=FormStartPosition.CenterScreen}){f.Controls.Add(new Label{Dock=DockStyle.Fill,Padding=new Padding(20),Text="保护弹窗预览\n\n需要确认：先冻结，确认后结束。\n不需要确认：直接结束，再告知结果。\n\n此预览没有制造故障，也没有操作进程。"});f.ShowDialog();}}
    void ExitTray(){if(MessageBox.Show("退出会停止自动结束/冻结监控；代理门卫仍运行，已冻结进程仍冻结。确定？","退出监控",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes){closing=true;timer.Dispose();icon.Visible=false;icon.Dispose();Application.Exit();}}
    protected override void OnFormClosing(FormClosingEventArgs e){if(!closing&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}base.OnFormClosing(e);}
}

public static class Program {
    static string InstanceSuffix {get{string d=AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);if(String.Equals(Path.GetFileName(d),"claude-gate",StringComparison.OrdinalIgnoreCase))return "";using(var hash=System.Security.Cryptography.SHA256.Create()){return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(d.ToLowerInvariant()))).Replace("-","").Substring(0,12);}}}
    public static string SignalName(string op){return "Local\\ClaudeExitGuard"+op+InstanceSuffix;}
    [STAThread] public static void Main(string[] args) {
        string dir=AppDomain.CurrentDomain.BaseDirectory;
        if(args.Length>0&&args[0]=="--recovery-test") {Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);var tray=new Tray(dir);tray.Shown+=async(s,e)=>await tray.TestRecovery(args[1]);Application.Run(tray);return;}
        if(args.Length>0&&args[0]=="--ui-preview") {Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);UiPreview(args[1]);return;}
        if(args.Length>0&&args[0]=="--ui-test") {Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);var tray=new Tray(dir);tray.Shown+=async(s,e)=>await tray.TestDashboard(args[1]);Application.Run(tray);return;}
        if(args.Length>0&&args[0]=="--node-ui-test") {Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);var tray=new Tray(dir);tray.Shown+=async(s,e)=>await tray.TestNodePicker(args[1]);Application.Run(tray);return;}
        if(args.Length>0&&args[0]=="--test-child") {while(true){File.AppendAllText(args[1],".");Thread.Sleep(50);}}
        if(args.Length>0&&args[0]=="--self-test") {SelfTest(args[1]);return;}
        bool created;using(var mutex=new Mutex(true,SignalName("Tray"),out created)) {
            bool show=!args.Contains("--background");
            bool desktop=args.Contains("--launch-desktop");
            if(!created){if(show){try{using(var signal=EventWaitHandle.OpenExisting(SignalName(desktop?"Desktop":"Open")))signal.Set();}catch{}}return;}Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try{var tray=new Tray(dir);if(show)tray.Shown+=(s,e)=>{tray.OpenDashboard();if(desktop)tray.RequestDesktop();};Application.Run(tray);}catch(Exception e){MessageBox.Show(e.ToString(),"Claude 保护未能启动",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
    }
    // Renders sample READY and BLOCKED screens (path, path-blocked.png) without touching processes or the guard.
    static void UiPreview(string image){
        var view=new Dashboard(Icon.ExtractAssociatedIcon(Application.ExecutablePath));
        Action<string> shot=file=>{view.Refresh();using(var b=new Bitmap(view.Width,view.Height)){view.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(file,System.Drawing.Imaging.ImageFormat.Png);}};
        view.Show();
        view.SetState("READY","固定出口验证通过，正在持续监测",DateTime.UtcNow.ToString("o"),0,"Example-A","203.0.113.10");
        view.SetObservations(new List<string[]>{new[]{"ipify","203.0.113.10","一致","ok"},new[]{"Cloudflare","203.0.113.10","一致","ok"}});
        view.Observed.Text="两路一致 · 203.0.113.10";view.Observed.ForeColor=Theme.GoodInk;view.Inspection.Text="16:42:08 实测 · 没有改变锁定状态";
        view.SetEvents(new List<string[]>{new[]{"16:20","正常","固定出口验证通过","good"},new[]{"16:19","恢复","复检通过，手动恢复","good"},new[]{"16:18","锁定","网络地址变化，自动复检未通过","bad"},new[]{"16:02","启动","门卫启动 · 网卡 WLAN","muted"}},"16:42 api.anthropic.com");
        Application.DoEvents();shot(image);
        view.SetState("BLOCKED","专用代理连接失败，保护保持锁定",DateTime.UtcNow.AddMinutes(-22).ToString("o"),3,"Example-A","203.0.113.10");
        view.SetObservations(new List<string[]>{new[]{"ipify","probe deadline exceeded","失败","bad"},new[]{"Cloudflare","probe deadline exceeded","失败","bad"}});
        view.Observed.Text="本次未能获取 IP";view.Observed.ForeColor=Theme.BadInk;
        view.ShowIncident("新启动的 Claude 也会被暂停，终端可能显示空白。「复检并恢复」通过出口验证后才恢复进程；「结束」只关闭进程，不会解锁。\n本轮新冻结 3 个进程；当前冻结 3 个。");
        view.IncidentAction.Text="尚未操作。继续使用请选择「复检并恢复」。";
        view.SetEvents(new List<string[]>{new[]{"16:41","锁定","专用代理连接失败，保护保持锁定","bad"},new[]{"16:41","重试","探测重试 3/3 · probe deadline exceeded","muted"},new[]{"16:20","正常","固定出口验证通过","good"}},"16:40 api.anthropic.com");
        Application.DoEvents();shot(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(image)),Path.GetFileNameWithoutExtension(image)+"-blocked.png"));
        Func<string,string,string,bool,Dictionary<string,object>> node=(n,server,type,ok)=>new Dictionary<string,object>{{"name",n},{"type",type},{"server",server},{"port",443},{"supported",ok},{"reason",ok?"":"该协议尚未支持隔离固定"}};
        var catalog=new Dictionary<string,object>{{"ok",true},{"file",@"C:\Users\me\clash\profile.yaml"},{"digest","0"},{"source","Clash 当前订阅"},
            {"nodes",new object[]{node("Example-A","203.0.113.10","vless",true),node("Example-B","198.51.100.20","vless",true),node("[其他节点]","[IPv4]","trojan",true),node("[域名节点]","[域名]","hysteria2",false)}}};
        using(var picker=new NodePicker(view.Icon,"Example-A",(a,r)=>Task.FromResult(catalog),t=>Task.FromResult(catalog))){
            picker.Show();for(int i=0;i<20;i++){Application.DoEvents();Thread.Sleep(20);}
            picker.SelectForPreview(1);Application.DoEvents();
            picker.Refresh();using(var b=new Bitmap(picker.Width,picker.Height)){picker.DrawToBitmap(b,new Rectangle(0,0,b.Width,b.Height));b.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(image)),Path.GetFileNameWithoutExtension(image)+"-picker.png"),System.Drawing.Imaging.ImageFormat.Png);}
        }
        view.Dispose();
    }
    static void Check(bool yes,string name,List<string> results){if(!yes)throw new Exception("FAIL "+name);results.Add("PASS "+name);}
    static void SelfTest(string output) {
        var results=new List<string>();string exe=Process.GetCurrentProcess().MainModule.FileName;
        string heartbeat=output+".heartbeat"; Process p=null;
        try {
            // Exact binary copy named uniquely: target matcher never touches installed Claude.
            string child=Path.Combine(Path.GetDirectoryName(output),"GuardTestChild.exe");File.Copy(exe,child,true);
            p=Process.Start(new ProcessStartInfo(child,"--test-child \""+heartbeat+"\""){UseShellExecute=false,CreateNoWindow=true});Thread.Sleep(350);
            var g=new ProcessGuard(new[]{child});
            Check(!g.Matches(exe),"unrelated supervisor excluded",results);
            var store=new ProcessGuard(new[]{@"C:\Program Files\WindowsApps\Claude_2.19675.0.0_x64__pzs8sxrjxfjjc\app\claude.exe"});
            Check(store.Matches(@"C:\Program Files\WindowsApps\Claude_2.20001.0.0_x64__pzs8sxrjxfjjc\app\claude.exe"),"store update keeps protection",results);
            Check(!store.Matches(@"C:\Program Files\WindowsApps\Claude_2.20001.0.0_x64__otherpublisher\app\claude.exe"),"other publisher excluded",results);
            Check(!store.Matches(@"C:\Program Files\WindowsApps\Claude_2.20001.0.0_x64__pzs8sxrjxfjjc\app\other\claude.exe"),"other subpath excluded",results);
            string r=g.Protect(false);Check(g.Held.Count==1,"confirm mode freezes exact target",results);
            Thread.Sleep(150);long n=new FileInfo(heartbeat).Length;Thread.Sleep(250);Check(new FileInfo(heartbeat).Length==n,"frozen child produces no work while waiting",results);
            Check(!p.HasExited,"cancel retains frozen process",results);
            Check(g.Release()=="","explicit resume succeeds",results);Thread.Sleep(200);Check(new FileInfo(heartbeat).Length>n,"resume restores child progress",results);
            g.Protect(false);g.Protect(false);Check(g.Held.Count==1,"repeated detection does not double suspend",results);
            g.Protect(true);Check(p.HasExited,"confirmation ends frozen child",results);
            p=Process.Start(new ProcessStartInfo(child,"--test-child \""+heartbeat+"\""){UseShellExecute=false,CreateNoWindow=true});Thread.Sleep(200);
            g.Protect(true);Check(p.HasExited,"automatic mode terminates running child",results);
            p=Process.Start(new ProcessStartInfo(child,"--test-child \""+heartbeat+"\""){UseShellExecute=false,CreateNoWindow=true});Thread.Sleep(200);
            g.Protect(false);p.Kill();p.WaitForExit();
            g.Protect(false);Check(g.Held.Count==0,"exited frozen child is pruned without resume",results);
            p=Process.Start(new ProcessStartInfo(child,"--test-child \""+heartbeat+"\""){UseShellExecute=false,CreateNoWindow=true});Thread.Sleep(200);
            g.Held.Add(new Held{pid=p.Id,start=1,path=child});g.Protect(false);
            Check(g.Held.Count==1 && g.Held[0].start!=1,"reused PID stale identity is removed",results);
            Check(g.Release()=="","valid identity still resumes after stale cleanup",results);g.Protect(true);
            using(var f=new Form()){var b=new Button();bool clicked=false;b.Click+=(s,e)=>clicked=true;f.Controls.Add(b);f.Show();b.PerformClick();Check(clicked,"WinForms action dispatch",results);f.Close();}
            File.WriteAllLines(output,results);
        }catch(Exception e){results.Add(e.ToString());File.WriteAllLines(output,results);Environment.ExitCode=1;}
        finally{if(p!=null){try{if(!p.HasExited)p.Kill();}catch{}}}
    }
}
