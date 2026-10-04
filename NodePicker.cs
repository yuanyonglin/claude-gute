using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed class NodePicker : Form {
    readonly Func<string,object,Task<Dictionary<string,object>>> command;
    readonly Func<string,Task<Dictionary<string,object>>> save;
    readonly ComboBox nodes=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList};
    readonly TextBox source=new TextBox{ReadOnly=true};
    readonly Label details=new Label(),result=new Label();
    readonly Button refresh=new Button{Text="读取当前 Clash"},browse=new Button{Text="选择 YAML…"},test=new Button{Text="测试所选节点"},apply=new Button{Text="保存为固定节点",Enabled=false};
    string file="",digest="",token="";bool busy,loaded;
    Func<string,string,MessageBoxIcon,bool> ask;
    public bool Applied;
    public sealed class Item {
        public string name,type,server,reason;public bool supported;public int port;
        public override string ToString(){return name+"  ·  "+type+(supported?"":"（暂不支持）");}
    }
    public NodePicker(Icon icon,string current,Func<string,object,Task<Dictionary<string,object>>> invoke,Func<string,Task<Dictionary<string,object>>> commit){
        command=invoke;save=commit;Text="选择 Claude 固定节点";Icon=icon;StartPosition=FormStartPosition.CenterParent;
        ask=(text,caption,iconType)=>MessageBox.Show(this,text,caption,MessageBoxButtons.YesNo,iconType)==DialogResult.Yes;
        ClientSize=new Size(720,450);MinimumSize=MaximumSize=Size;Font=new Font("Microsoft YaHei UI",10);BackColor=Color.FromArgb(244,246,248);
        var title=new Label{Text="固定出口独立于主 Clash 的临时选择",Font=new Font(Font,FontStyle.Bold),Bounds=new Rectangle(22,20,675,28)};
        var currentLabel=new Label{Text="当前固定："+current,Bounds=new Rectangle(22,53,675,26)};
        source.SetBounds(22,91,445,28);refresh.SetBounds(478,89,112,32);browse.SetBounds(597,89,102,32);
        nodes.SetBounds(22,139,676,32);details.SetBounds(22,181,676,48);result.SetBounds(22,245,676,109);
        result.Text="请选择节点，再测试。测试不会改变主 Clash 或 Claude 固定配置。\n节点名字不是地理证明；测试会显示两路实际 IP 和 Cloudflare 国家码。";
        test.SetBounds(22,378,170,40);apply.SetBounds(208,378,190,40);
        var cancel=new Button{Text="关闭",Bounds=new Rectangle(588,378,110,40)};cancel.Click+=(s,e)=>Close();
        Controls.AddRange(new Control[]{title,currentLabel,source,refresh,browse,nodes,details,result,test,apply,cancel});
        refresh.Click+=async(s,e)=>await LoadNodes("");
        browse.Click+=async(s,e)=>{using(var d=new OpenFileDialog{Filter="Clash YAML|*.yaml;*.yml",CheckFileExists=true})if(d.ShowDialog(this)==DialogResult.OK)await LoadNodes(d.FileName);};
        nodes.SelectedIndexChanged+=(s,e)=>{token="";apply.Enabled=false;var n=nodes.SelectedItem as Item;details.Text=n==null?"":n.server+":"+n.port+"  ·  "+n.type+(n.supported?"":"\n"+n.reason);result.Text="选择尚未保存。请测试后核对出口，再确认保存。";test.Enabled=!busy&&n!=null&&n.supported;};
        test.Click+=async(s,e)=>await TestNode();apply.Click+=async(s,e)=>await ApplyNode();Shown+=async(s,e)=>await LoadNodes("");
        FormClosing+=(s,e)=>{if(busy){e.Cancel=true;MessageBox.Show(this,"操作仍在进行，请等结果返回。","请稍候");}};
    }
    void Busy(bool value){busy=value;refresh.Enabled=browse.Enabled=nodes.Enabled=!value;var n=nodes.SelectedItem as Item;test.Enabled=!value&&n!=null&&n.supported;apply.Enabled=!value&&token!="";}
    async Task LoadNodes(string selectedFile){
        token="";Busy(true);result.Text="读取本地配置…";
        try{var c=await command("list",new{file=selectedFile});file=Convert.ToString(c["file"]);digest=Convert.ToString(c["digest"]);source.Text=file;nodes.Items.Clear();
            foreach(Dictionary<string,object> n in (IEnumerable)c["nodes"])nodes.Items.Add(new Item{name=Convert.ToString(n["name"]),type=Convert.ToString(n["type"]),server=Convert.ToString(n["server"]),port=Convert.ToInt32(n["port"]),supported=Convert.ToBoolean(n["supported"]),reason=Convert.ToString(n["reason"])});
            // Never preselect the main Clash selector or silently save its temporary node.
            result.Text="已读取 "+nodes.Items.Count+" 个真实节点。请选择 Claude 专用固定节点。\n来源："+Convert.ToString(c["source"]);
        }catch(Exception e){nodes.Items.Clear();result.Text=e.Message;}finally{Busy(false);loaded=true;}
    }
    async Task TestNode(){
        var n=(Item)nodes.SelectedItem;
        if(!ask("确认测试："+n.name+"？\n仅启动隔离测试，不改变主 Clash、不恢复 Claude。","第一次确认：候选节点",MessageBoxIcon.Question))return;
        token="";Busy(true);result.Text="正在隔离测试两路实际出口，请稍候…";
        try{var r=await command("test",new{file=file,name=n.name,digest=digest});token=Convert.ToString(r["token"]);
            result.Text="测试通过："+Convert.ToString(r["name"])+"\nipify / Cloudflare 一致："+Convert.ToString(r["ip"])+"\nCloudflare 国家码："+Convert.ToString(r["country"])+"（不是精确城市证明）\n尚未保存；请核对后点击「保存为固定节点」。";
        }catch(Exception e){result.Text="测试未通过，没有修改固定节点。\n"+e.Message;}finally{Busy(false);}
    }
    async Task ApplyNode(){
        if(!ask(result.Text+"\n\n确认将它保存为默认固定节点？\n将锁定 Claude 并重启专用代理；主 Clash 不变。\n旧 Claude 进程恢复后的新连接也会使用新出口。保存后仍保持锁定。","第二次确认：保存固定节点",MessageBoxIcon.Warning))return;
        Busy(true);result.Text="复测并保存中。Claude 保持锁定…";
        try{var r=await save(token);Applied=true;token="";result.Text="已保存："+r["name"]+"\n固定 IP："+r["expectedIp"]+"\n仍保持锁定。请关闭此窗口，核对后再选择「复检并恢复」。";}
        catch(Exception e){token="";result.Text="保存未完成："+e.Message;}
        finally{Busy(false);}
    }
    public async Task TestWorkflow(string image){
        for(int i=0;i<200&&!loaded;i++)await Task.Delay(50);
        if(!loaded)throw new Exception("Node catalog load did not complete");
        while(busy)await Task.Delay(50);
        if(nodes.Items.Count!=6)throw new Exception("Expected six real Clash nodes; actual="+nodes.Items.Count+"; "+result.Text);
        foreach(Item n in nodes.Items)if(n.name=="Example-B🐎")nodes.SelectedItem=n;
        if(nodes.SelectedItem==null||!details.Text.Contains("198.51.100.20"))throw new Exception("Exact Unicode node selection failed");
        int confirmations=0;ask=(text,title,iconType)=>{confirmations++;return false;};
        test.PerformClick();if(token!=""||busy)throw new Exception("Cancel test changed state");
        ask=(text,title,iconType)=>{confirmations++;return true;};test.PerformClick();while(busy)await Task.Delay(50);
        if(token==""||!apply.Enabled)throw new Exception("Real UI test failed: "+result.Text);
        ask=(text,title,iconType)=>{confirmations++;return false;};apply.PerformClick();if(Applied||busy)throw new Exception("Cancel save changed state");
        ask=(text,title,iconType)=>{confirmations++;return true;};apply.PerformClick();while(busy)await Task.Delay(50);
        if(!Applied||token!=""||apply.Enabled||confirmations!=4)throw new Exception("UI save failed: "+result.Text);
        Refresh();using(var bitmap=new Bitmap(Width,Height)){DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(image,System.Drawing.Imaging.ImageFormat.Png);}
        System.IO.File.WriteAllText(image+".txt","PASS six real nodes\nPASS exact emoji name through UTF8 subprocess\nPASS cancel test and save leave state unchanged\nPASS real test and real save buttons\nPASS both confirmations\n"+result.Text);
    }
}
