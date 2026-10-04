using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

// Main window: status banner (actions follow the state), exit check, recent events, launch bar.
// The window never resizes itself; the incident section expands inside the banner.
public sealed class Dashboard : Form {
    public readonly Label StateTitle,StateDetail,Node,Expected,Observed,LastCheck,Processes,Inspection,Mode,LastConnect,HistorySummary;
    public readonly Chip IpWarning;
    public readonly RadioButton Confirm,Auto;
    public readonly FlatBtn Verify,Resume,Alert,Logs,Code,Desktop,SelectNode,Settings,History;
    public readonly TableLayoutPanel Incident;
    public readonly Label IncidentText,IncidentAction;
    public readonly FlatBtn IncidentRecover,IncidentEnd,IncidentDismiss;
    public bool IncidentVisible;
    public bool Updating;
    readonly TableLayoutPanel root,observations,events;
    readonly RoundPanel banner;
    readonly StatusIcon status;
    readonly Form settingsForm;
    readonly List<Label> wrapping=new List<Label>();
    string phase="STARTING",shownEvents="";

    public Dashboard(Icon icon){
        AutoScaleDimensions=new SizeF(96F,96F);AutoScaleMode=AutoScaleMode.Dpi;
        Text="Claude Guard";Icon=icon;StartPosition=FormStartPosition.CenterScreen;
        Font=Theme.Font(10f);ForeColor=Theme.Ink;BackColor=Theme.Ground;
        ClientSize=new Size(690,760);MinimumSize=SizeFromClientSize(new Size(600,700));

        root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,Padding=new Padding(20,14,20,16)};
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        foreach(var h in new[]{SizeType.AutoSize,SizeType.AutoSize,SizeType.AutoSize,SizeType.Percent,SizeType.AutoSize})root.RowStyles.Add(new RowStyle(h,100));
        Controls.Add(root);

        // Header
        var header=Row(3);header.Margin=new Padding(0,0,0,10);
        header.Controls.Add(new PictureBox{Image=icon.ToBitmap(),Size=new Size(32,32),SizeMode=PictureBoxSizeMode.Zoom,Margin=new Padding(0,0,10,0)},0,0);
        var name=Theme.Text("Claude Guard",12.5f,Theme.Ink,FontStyle.Bold);name.Anchor=AnchorStyles.Left;header.Controls.Add(name,1,0);
        Settings=new FlatBtn("设置",ButtonKind.Link,Theme.Muted){Anchor=AnchorStyles.Right,Margin=new Padding(0)};header.Controls.Add(Settings,2,0);
        root.Controls.Add(header,0,0);

        // Status banner
        banner=new RoundPanel{Fill=Theme.WaitTint,Padding=new Padding(18,16,18,16)};
        var top=Row(3);
        status=new StatusIcon{Anchor=AnchorStyles.Left|AnchorStyles.Top};top.Controls.Add(status,0,0);
        var titles=Stack();
        StateTitle=Theme.Text("正在读取保护状态",14f,Theme.WaitInk,FontStyle.Bold);
        StateDetail=Wrap(Theme.Text("",10f,Theme.Body));StateDetail.Margin=new Padding(0,4,0,0);
        titles.Controls.Add(StateTitle);titles.Controls.Add(StateDetail);top.Controls.Add(titles,1,0);
        Resume=new FlatBtn("复检并恢复",ButtonKind.Primary,Theme.Bad){Visible=false,Margin=new Padding(10,0,0,0),Anchor=AnchorStyles.Right|AnchorStyles.Top};top.Controls.Add(Resume,2,0);
        banner.Controls.Add(top);
        var chips=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Margin=new Padding(54,12,0,0),Dock=DockStyle.Fill};
        Processes=new Chip{Text="无冻结进程"};LastCheck=new Chip{Text="尚无通过记录"};Mode=new Chip{Text="异常时先冻结"};
        IpWarning=new Chip{Text="IP 变化频繁",ForeColor=Theme.BadInk,Visible=false,Font=Theme.Font(9.5f,FontStyle.Bold)};
        chips.Controls.AddRange(new Control[]{IpWarning,Processes,LastCheck,Mode});banner.Controls.Add(chips);
        Incident=Stack();Incident.Visible=false;Incident.Margin=new Padding(54,14,0,0);
        IncidentText=Wrap(Theme.Text("",9.5f,Theme.Body));
        IncidentAction=Wrap(Theme.Text("",9.5f,Theme.BadInk,FontStyle.Bold));IncidentAction.Margin=new Padding(0,6,0,0);
        var incidentButtons=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,WrapContents=true,Margin=new Padding(0,12,0,0)};
        IncidentRecover=new FlatBtn("复检并恢复",ButtonKind.Primary,Theme.Bad);
        IncidentEnd=new FlatBtn("结束已识别的 Claude",ButtonKind.Secondary,Theme.BadInk){AccentBorder=Theme.BadBorder};
        IncidentDismiss=new FlatBtn("稍后处理（保持冻结）",ButtonKind.Link,Theme.Muted){Height=42};
        incidentButtons.Controls.AddRange(new Control[]{IncidentRecover,IncidentEnd,IncidentDismiss});
        Incident.Controls.Add(IncidentText);Incident.Controls.Add(IncidentAction);Incident.Controls.Add(incidentButtons);
        banner.Controls.Add(Incident);
        root.Controls.Add(banner,0,1);

        // Fixed exit
        var exit=new RoundPanel{Fill=Theme.Surface,Stroke=Theme.Border};
        var exitHead=Row(2);exitHead.Controls.Add(Anchored(Theme.Text("固定出口",9f,Theme.Muted),AnchorStyles.Left),0,0);
        SelectNode=new FlatBtn("更换节点",ButtonKind.Link,Theme.Link){Anchor=AnchorStyles.Right,Margin=new Padding(0)};exitHead.Controls.Add(SelectNode,1,0);
        exit.Controls.Add(exitHead);
        var nodeRow=Row(2);nodeRow.Margin=new Padding(0,2,0,8);
        Node=Anchored(Theme.Text("读取中",13f,Theme.Ink,FontStyle.Bold),AnchorStyles.Left);nodeRow.Controls.Add(Node,0,0);
        Expected=Anchored(new Label{Text="—",Font=new Font(Theme.Mono,10.5f),ForeColor=Theme.Body,AutoSize=true,Margin=new Padding(0)},AnchorStyles.Right);nodeRow.Controls.Add(Expected,1,0);
        exit.Controls.Add(nodeRow);
        exit.Controls.Add(Divider());
        observations=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=3,Dock=DockStyle.Fill,Margin=new Padding(0)};
        observations.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));observations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));observations.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        exit.Controls.Add(observations);
        exit.Controls.Add(Divider());
        var checkRow=Row(2);
        var checkText=Stack();
        Observed=Theme.Text("尚未实测",10f,Theme.Ink,FontStyle.Bold);
        Inspection=Wrap(Theme.Text("打开窗口时会自动实测一次；实测不会解除锁定，也不会恢复进程。",9f,Theme.Muted));Inspection.Margin=new Padding(0,2,0,0);
        checkText.Controls.Add(Observed);checkText.Controls.Add(Inspection);checkRow.Controls.Add(checkText,0,0);
        Verify=new FlatBtn("重新验证",ButtonKind.Secondary,Theme.Ink){Height=34,Margin=new Padding(10,0,0,0),Anchor=AnchorStyles.Right};checkRow.Controls.Add(Verify,1,0);
        exit.Controls.Add(checkRow);
        var historyRow=Row(2);historyRow.Margin=new Padding(0,6,0,0);
        HistorySummary=Anchored(Theme.Text("IP 变化：暂无记录",9f,Theme.Muted),AnchorStyles.Left);historyRow.Controls.Add(HistorySummary,0,0);
        History=new FlatBtn("IP 历史",ButtonKind.Link,Theme.Link){Anchor=AnchorStyles.Right,Margin=new Padding(0)};historyRow.Controls.Add(History,1,0);
        exit.Controls.Add(historyRow);
        root.Controls.Add(exit,0,2);
        SetObservations(new List<string[]>());

        // Recent events
        var log=new RoundPanel{Fill=Theme.Surface,Stroke=Theme.Border,AutoSize=false};
        log.RowStyles.Add(new RowStyle(SizeType.AutoSize));log.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var logHead=Row(2);logHead.Margin=new Padding(0,0,0,6);
        logHead.Controls.Add(Anchored(Theme.Text("最近事件",9f,Theme.Muted),AnchorStyles.Left),0,0);
        LastConnect=Anchored(Theme.Text("",9f,Theme.Muted),AnchorStyles.Right);logHead.Controls.Add(LastConnect,1,0);
        log.Controls.Add(logHead,0,0);
        events=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,Margin=new Padding(0)};
        events.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,52));events.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,56));events.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        log.Controls.Add(events,0,1);
        root.Controls.Add(log,0,3);

        // Launch bar
        var footer=Row(5);footer.Margin=new Padding(0,2,0,0);
        footer.ColumnStyles.Clear();foreach(var w in new[]{0,0,100,0,0})footer.ColumnStyles.Add(w==0?new ColumnStyle(SizeType.AutoSize):new ColumnStyle(SizeType.Percent,100));
        Code=new FlatBtn("启动 Claude Code",ButtonKind.Primary,Theme.Ink);Desktop=new FlatBtn("启动桌面版",ButtonKind.Secondary,Theme.Ink);
        Alert=new FlatBtn("状态详情",ButtonKind.Link,Theme.Link){Anchor=AnchorStyles.Right};Logs=new FlatBtn("打开日志",ButtonKind.Link,Theme.Link){Anchor=AnchorStyles.Right,Margin=new Padding(0)};
        footer.Controls.Add(Code,0,0);footer.Controls.Add(Desktop,1,0);footer.Controls.Add(Alert,3,0);footer.Controls.Add(Logs,4,0);
        root.Controls.Add(footer,0,4);

        // Settings: the termination mode is rare to change, so it lives in a small dialog.
        settingsForm=new Form{Text="设置",FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false,StartPosition=FormStartPosition.CenterParent,
            BackColor=Theme.Ground,Font=Theme.Font(10f),ForeColor=Theme.Ink,AutoScaleDimensions=new SizeF(96F,96F),AutoScaleMode=AutoScaleMode.Dpi,ClientSize=new Size(440,250),Icon=icon};
        var form=Stack();form.Dock=DockStyle.Fill;form.Padding=new Padding(22,18,22,18);
        form.Controls.Add(Theme.Text("检测到异常时",11f,Theme.Ink,FontStyle.Bold));
        Confirm=new RadioButton{Text="先冻结，确认后再结束（推荐）",AutoSize=true,Checked=true,Margin=new Padding(0,12,0,0)};
        Auto=new RadioButton{Text="直接结束全部 Claude",AutoSize=true,Margin=new Padding(0,10,0,0)};
        form.Controls.Add(Confirm);form.Controls.Add(Hint("冻结的进程在复检通过后原样恢复。"));
        form.Controls.Add(Auto);form.Controls.Add(Hint("会立即中断正在进行的任务。"));
        var done=new FlatBtn("完成",ButtonKind.Primary,Theme.Ink){Margin=new Padding(0,16,0,0),DialogResult=DialogResult.OK};
        form.Controls.Add(done);settingsForm.AcceptButton=done;settingsForm.Controls.Add(form);
        Settings.Click+=(s,e)=>settingsForm.ShowDialog(this);
        EventHandler mode=(s,e)=>{Mode.Text=Auto.Checked?"异常时直接结束":"异常时先冻结";};
        Confirm.CheckedChanged+=mode;Auto.CheckedChanged+=mode;

        Resize+=(s,e)=>FitText();Shown+=(s,e)=>FitText();
        FormClosing+=(s,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
    }

    static TableLayoutPanel Row(int columns){
        var t=new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=columns,RowCount=1,Dock=DockStyle.Fill,Margin=new Padding(0)};
        for(int i=0;i<columns;i++)t.ColumnStyles.Add(i==columns-2||columns==2&&i==0?new ColumnStyle(SizeType.Percent,100):new ColumnStyle(SizeType.AutoSize));
        return t;
    }
    static TableLayoutPanel Stack(){return new TableLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Dock=DockStyle.Fill,Margin=new Padding(0)};}
    static Label Anchored(Label l,AnchorStyles a){l.Anchor=a;return l;}
    static Label Hint(string text){var l=Theme.Text(text,9f,Theme.Muted);l.Margin=new Padding(20,2,0,0);return l;}
    static Panel Divider(){return new Panel{Height=1,Dock=DockStyle.Top,BackColor=Theme.Divider,Margin=new Padding(0,0,0,0)};}
    Label Wrap(Label l){wrapping.Add(l);return l;}
    // AutoSize labels only wrap when given a maximum width; recompute it from the window width.
    void FitText(){
        int width=Math.Max(240,root.ClientSize.Width-root.Padding.Horizontal-36);
        foreach(var l in wrapping){
            int indent=l==Inspection?Verify.Width+12:l==StateDetail&&Resume.Visible?54+Resume.Width+10:54;
            l.MaximumSize=new Size(width-indent,0);
        }
    }

    public void SetState(string phase,string reason,string last,int held,string node,string expected){
        this.phase=phase;
        if(phase=="READY"){banner.Fill=Theme.GoodTint;status.Set(StatusGlyph.Check,Theme.Good);StateTitle.ForeColor=Theme.GoodInk;StateTitle.Text="保护中 · 固定出口已验证";}
        else if(phase=="STARTING"){banner.Fill=Theme.WaitTint;status.Set(StatusGlyph.Pending,Theme.Wait);StateTitle.ForeColor=Theme.WaitInk;StateTitle.Text="正在验证出口";}
        else{banner.Fill=Theme.BadTint;status.Set(StatusGlyph.Lock,Theme.Bad);StateTitle.ForeColor=Theme.BadInk;StateTitle.Text="已锁定 · Claude 网络已阻断";}
        StateDetail.Text=reason;
        Processes.Text=held>0?"已冻结 "+held+" 个 Claude 进程":"无冻结进程";Processes.ForeColor=held>0?Theme.BadInk:Theme.Body;
        DateTime at;LastCheck.Text=DateTime.TryParse(last,out at)?"上次通过 "+at.ToLocalTime().ToString("MM-dd HH:mm:ss"):"尚无通过记录";
        Node.Text=node;Expected.Text=expected;
        Resume.Visible=phase=="BLOCKED"&&!IncidentVisible;
        FitText();
    }
    // rows: {source, value, verdict, "ok"|"bad"}; empty shows a placeholder.
    public void SetObservations(IList<string[]> rows){
        observations.SuspendLayout();
        foreach(Control c in observations.Controls.Cast<Control>().ToArray())c.Dispose();
        observations.RowStyles.Clear();
        if(rows.Count==0)rows=new List<string[]>{new[]{"ipify","—",""," "},new[]{"Cloudflare","—",""," "}};
        for(int i=0;i<rows.Count;i++){
            var r=rows[i];bool ok=r[3]=="ok";observations.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            observations.Controls.Add(Cell(Theme.Text(r[0],10f,Theme.Muted)),0,i);
            observations.Controls.Add(Cell(new Label{Text=r[1],Font=r[1].Any(ch=>ch>127)?Theme.Font(9.5f):new Font(Theme.Mono,10f),ForeColor=Theme.Ink,AutoSize=true,AutoEllipsis=true,Margin=new Padding(0)}),1,i);
            observations.Controls.Add(Cell(Theme.Text(r[2],9.5f,ok?Theme.GoodInk:Theme.BadInk,FontStyle.Bold)),2,i);
        }
        observations.ResumeLayout();
    }
    static Label Cell(Label l){l.Margin=new Padding(0,7,0,7);l.Anchor=AnchorStyles.Left;return l;}
    // rows: {time, kind, text, "good"|"bad"|"muted"}; newest first.
    public void SetEvents(IList<string[]> rows,string lastConnect){
        LastConnect.Text=lastConnect==null?"":"最近连接 "+lastConnect;
        string key=string.Join("\n",rows.Select(r=>string.Join("|",r)));if(key==shownEvents)return;shownEvents=key;
        events.SuspendLayout();
        foreach(Control c in events.Controls.Cast<Control>().ToArray())c.Dispose();
        events.RowStyles.Clear();
        if(rows.Count==0)rows=new List<string[]>{new[]{"","","暂无事件","muted"}};
        for(int i=0;i<rows.Count;i++){
            var r=rows[i];var tone=r[3]=="good"?Theme.GoodInk:r[3]=="bad"?Theme.BadInk:Theme.Muted;
            events.RowStyles.Add(new RowStyle(SizeType.Absolute,26));
            events.Controls.Add(new Label{Text=r[0],Font=new Font(Theme.Mono,9.5f),ForeColor=Theme.Muted,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0)},0,i);
            events.Controls.Add(new Label{Text=r[1],Font=Theme.Font(9.5f,FontStyle.Bold),ForeColor=tone,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0)},1,i);
            events.Controls.Add(new Label{Text=r[2],Font=Theme.Font(9.5f),ForeColor=Theme.Body,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,Margin=new Padding(0)},2,i);
        }
        // Trailing filler row: otherwise the table stretches the last event row to its full height.
        events.RowStyles.Add(new RowStyle(SizeType.Percent,100));events.RowCount=rows.Count+1;
        events.ResumeLayout();
    }
    public void SetHistory(int day,int week,bool frequent){
        HistorySummary.Text="IP 变化：近 24 小时 "+day+" 次 · 近 7 天 "+week+" 次"+(frequent?"（频繁）":"");
        HistorySummary.ForeColor=frequent?Theme.BadInk:Theme.Muted;HistorySummary.Font=Theme.Font(9f,frequent?FontStyle.Bold:FontStyle.Regular);
        IpWarning.Visible=frequent;
    }
    // rows: {time, type, node, detail, "bad"|"muted"}; newest first.
    public void ShowHistory(IList<string[]> rows){
        using(var f=new Form{Text="出口 IP 历史",StartPosition=FormStartPosition.CenterParent,ShowInTaskbar=false,MinimizeBox=false,BackColor=Theme.Ground,Font=Theme.Font(9.5f),
            AutoScaleDimensions=new SizeF(96F,96F),AutoScaleMode=AutoScaleMode.Dpi,ClientSize=new Size(720,460),Icon=Icon}){
            var list=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,HeaderStyle=ColumnHeaderStyle.Nonclickable,BorderStyle=BorderStyle.None};
            list.Columns.Add("时间",130);list.Columns.Add("类型",100);list.Columns.Add("节点",160);list.Columns.Add("IP / 说明",300);
            foreach(var r in rows){var item=new ListViewItem(new[]{r[0],r[1],r[2],r[3]});if(r[4]=="bad")item.ForeColor=Theme.BadInk;list.Items.Add(item);}
            if(rows.Count==0)list.Items.Add(new ListViewItem(new[]{"","暂无记录","",""}));
            var note=Theme.Text("红色为非主动变化：节点出口变了、配置被界面以外的方式修改，或实测 IP 与预期不符。",9f,Theme.Muted);note.Dock=DockStyle.Bottom;note.Padding=new Padding(10,8,10,8);note.AutoSize=false;note.Height=34;
            f.Controls.Add(list);f.Controls.Add(note);f.ShowDialog(this);
        }
    }
    public void ShowIncident(string text){
        IncidentText.Text=text;if(IncidentVisible)return;
        IncidentVisible=true;Incident.Visible=true;Resume.Visible=false;FitText();
    }
    public void HideIncident(){
        if(!IncidentVisible)return;
        IncidentVisible=false;Incident.Visible=false;Resume.Visible=phase=="BLOCKED";FitText();
    }
}
