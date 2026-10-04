using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed class NodePicker : Form {
    readonly Func<string,object,Task<Dictionary<string,object>>> command;
    readonly Func<string,Task<Dictionary<string,object>>> save;
    readonly ListBox nodes=new ListBox{DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=54,BorderStyle=BorderStyle.None,Dock=DockStyle.Fill,IntegralHeight=false,Margin=new Padding(0)};
    readonly TextBox source=new TextBox{ReadOnly=true,BorderStyle=BorderStyle.FixedSingle,BackColor=Theme.Surface,Dock=DockStyle.Fill,Margin=new Padding(0,4,10,0)};
    readonly Label details=Theme.Text("",9f,Theme.Muted),result=Theme.Text("",9.5f,Theme.Body);
    readonly Button refresh=new FlatBtn("读取当前 Clash",ButtonKind.Secondary,Theme.Ink){Height=34},browse=new FlatBtn("选择文件…",ButtonKind.Secondary,Theme.Ink){Height=34,Margin=new Padding(0)},
        test=new FlatBtn("测试所选节点",ButtonKind.Secondary,Theme.Ink),apply=new FlatBtn("保存为固定节点",ButtonKind.Primary,Theme.Link){Enabled=false};
    readonly string current;
    static readonly Font NameFont=Theme.Font(10f,FontStyle.Bold),MetaFont=new Font(Theme.Mono,9f),NoteFont=Theme.Font(9f);
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
        this.current=current;
        AutoScaleDimensions=new SizeF(96F,96F);AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new Size(580,660);MinimumSize=SizeFromClientSize(new Size(520,600));Font=Theme.Font(10f);ForeColor=Theme.Ink;BackColor=Theme.Ground;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,Padding=new Padding(20,16,20,16)};
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        foreach(var h in new[]{SizeType.AutoSize,SizeType.AutoSize,SizeType.AutoSize,SizeType.Percent,SizeType.AutoSize,SizeType.AutoSize,SizeType.AutoSize})root.RowStyles.Add(new RowStyle(h,100));
        root.Controls.Add(Theme.Text("更换固定节点",13f,Theme.Ink,FontStyle.Bold),0,0);
        var currentLabel=Theme.Text("当前："+current+"。测试在隔离核心中进行，不改变主 Clash。",9.5f,Theme.Muted);currentLabel.Margin=new Padding(0,4,0,12);root.Controls.Add(currentLabel,0,1);
        var sourceRow=new TableLayoutPanel{AutoSize=true,ColumnCount=3,Dock=DockStyle.Fill,Margin=new Padding(0,0,0,12)};
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceRow.Controls.Add(source,0,0);sourceRow.Controls.Add(refresh,1,0);sourceRow.Controls.Add(browse,2,0);root.Controls.Add(sourceRow,0,2);
        var list=new RoundPanel{Fill=Theme.Surface,Stroke=Theme.Border,AutoSize=false,Padding=new Padding(1,6,1,6),Margin=new Padding(0,0,0,8)};
        list.Controls.Add(nodes);root.Controls.Add(list,0,3);
        details.Margin=new Padding(2,0,0,10);root.Controls.Add(details,0,4);
        var resultCard=new RoundPanel{Fill=Theme.LinkTint,Padding=new Padding(16,12,16,12)};resultCard.Controls.Add(result);root.Controls.Add(resultCard,0,5);
        result.Text="请选择节点，再测试。节点名字不是地理证明；测试会显示两路实际 IP 和 Cloudflare 国家码。";
        var footer=new TableLayoutPanel{AutoSize=true,ColumnCount=4,Dock=DockStyle.Fill,Margin=new Padding(0)};
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var cancel=new FlatBtn("关闭",ButtonKind.Link,Theme.Link){Height=42,Margin=new Padding(0)};cancel.Click+=(s,e)=>Close();
        footer.Controls.Add(test,0,0);footer.Controls.Add(apply,1,0);footer.Controls.Add(cancel,3,0);root.Controls.Add(footer,0,6);
        Controls.Add(root);
        Resize+=(s,e)=>{result.MaximumSize=details.MaximumSize=new Size(Math.Max(200,root.ClientSize.Width-root.Padding.Horizontal-40),0);};
        nodes.DrawItem+=DrawNode;
        refresh.Click+=async(s,e)=>await LoadNodes("");
        browse.Click+=async(s,e)=>{using(var d=new OpenFileDialog{Filter="Clash YAML|*.yaml;*.yml",CheckFileExists=true})if(d.ShowDialog(this)==DialogResult.OK)await LoadNodes(d.FileName);};
        nodes.SelectedIndexChanged+=(s,e)=>{token="";apply.Enabled=false;var n=nodes.SelectedItem as Item;details.Text=n==null?"":n.server+":"+n.port+"  ·  "+n.type+(n.supported?"":"\n"+n.reason);result.Text="选择尚未保存。请测试后核对出口，再确认保存。";test.Enabled=!busy&&n!=null&&n.supported;};
        test.Click+=async(s,e)=>await TestNode();apply.Click+=async(s,e)=>await ApplyNode();Shown+=async(s,e)=>await LoadNodes("");
        FormClosing+=(s,e)=>{if(busy){e.Cancel=true;MessageBox.Show(this,"操作仍在进行，请等结果返回。","请稍候");}};
    }
    // Row: radio mark, name, server and protocol, and a note for the current or unsupported node.
    void DrawNode(object sender,DrawItemEventArgs e){
        if(e.Index<0)return;var n=(Item)nodes.Items[e.Index];var g=e.Graphics;
        bool on=(e.State&DrawItemState.Selected)!=0;var r=e.Bounds;
        using(var b=new SolidBrush(on?Color.FromArgb(242,246,252):Theme.Surface))g.FillRectangle(b,r);
        if(e.Index<nodes.Items.Count-1)using(var pen=new Pen(Theme.Divider))g.DrawLine(pen,r.Left+16,r.Bottom-1,r.Right-16,r.Bottom-1);
        float k=g.DpiX/96f;int x=r.Left+(int)(16*k),cy=r.Top+r.Height/2,d=(int)(16*k);
        g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using(var pen=new Pen(on?Theme.Link:Color.FromArgb(183,190,200),on?5*k:1.5f*k)){float inset=on?2.5f*k:0.75f*k;g.DrawEllipse(pen,x+inset,cy-d/2+inset,d-2*inset,d-2*inset);}
        int tx=x+d+(int)(12*k);string note=n.name==current?"当前":n.supported?"":"暂不支持";
        var noteSize=TextRenderer.MeasureText(note,NoteFont);
        var textRect=new Rectangle(tx,r.Top+(int)(7*k),r.Right-tx-noteSize.Width-(int)(24*k),r.Height/2);
        TextRenderer.DrawText(g,n.name,NameFont,textRect,n.supported?Theme.Ink:Theme.Disabled,TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g,n.server+":"+n.port+" · "+n.type,MetaFont,new Rectangle(tx,cy+(int)(1*k),textRect.Width,r.Height/2-(int)(4*k)),Theme.Muted,TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        if(note!="")TextRenderer.DrawText(g,note,NoteFont,new Rectangle(r.Right-noteSize.Width-(int)(16*k),r.Top,noteSize.Width,r.Height),Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.Left);
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
    public void SelectForPreview(int index){nodes.SelectedIndex=index;}
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
