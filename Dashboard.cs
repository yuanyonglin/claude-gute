using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

public sealed class Card : Panel {
    public Card(){DoubleBuffered=true;BackColor=Color.White;Padding=new Padding(20);Margin=new Padding(0,0,0,14);}
    protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);using(var pen=new Pen(Color.FromArgb(226,229,234)))e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);}
}
public sealed class Dashboard : Form {
    static readonly Color Ink=Color.FromArgb(33,44,61),Muted=Color.FromArgb(103,114,129),Accent=Color.FromArgb(31,102,87);
    public readonly Label StateTitle,StateDetail,Node,Expected,Observed,LastCheck,Processes,Inspection;
    public readonly RadioButton Confirm,Auto;
    public readonly Button Verify,Resume,Alert,Logs,Code,Desktop,SelectNode;
    public readonly Card Incident;
    public readonly Label IncidentText,IncidentAction;
    public readonly Button IncidentRecover,IncidentEnd,IncidentDismiss;
    readonly RowStyle incidentStyle;
    public bool IncidentVisible;
    public bool Updating;
    public Dashboard(Icon icon){
        Text="Claude Guard · 网络保护";Icon=icon;StartPosition=FormStartPosition.CenterScreen;ClientSize=new Size(940,745);MinimumSize=new Size(860,760);
        Font=new Font("Microsoft YaHei UI",10);ForeColor=Ink;BackColor=Color.FromArgb(244,246,248);AutoScaleMode=AutoScaleMode.Dpi;
        var root=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(26,22,26,20),ColumnCount=1,RowCount=8};
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,70));
        root.RowStyles.Add(incidentStyle=new RowStyle(SizeType.Absolute,0));
        foreach(int h in new[]{106,163,108,138,65})root.RowStyles.Add(new RowStyle(SizeType.Absolute,h));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(root);
        var header=new Panel{Dock=DockStyle.Fill};header.Controls.Add(new PictureBox{Image=icon.ToBitmap(),Bounds=new Rectangle(0,0,42,42),SizeMode=PictureBoxSizeMode.Zoom});
        header.Controls.Add(Label("Claude Guard",20,FontStyle.Bold, new Rectangle(55,-3,340,37),Ink));
        header.Controls.Add(Label("网络保护 · Claude Code 与桌面应用",10,FontStyle.Regular,new Rectangle(57,35,470,25),Muted));
        var badge=Label("固定出口",10,FontStyle.Bold,new Rectangle(736,8,120,30),Accent);badge.TextAlign=ContentAlignment.MiddleCenter;badge.BackColor=Color.FromArgb(225,239,233);header.Controls.Add(badge);root.Controls.Add(header,0,0);
        Incident=new Card{Dock=DockStyle.Fill,Visible=false,Margin=new Padding(0,0,0,14)};
        var inner=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(0)};
        inner.RowStyles.Add(new RowStyle(SizeType.Percent,100));inner.RowStyles.Add(new RowStyle(SizeType.Absolute,30));inner.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
        IncidentText=new Label{Dock=DockStyle.Fill,AutoSize=false,ForeColor=Color.FromArgb(150,88,41)};
        IncidentAction=new Label{Dock=DockStyle.Fill,AutoSize=false,ForeColor=Accent};
        var incidentButtons=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=new Padding(0)};
        IncidentRecover=Button("复检并恢复…",Accent,true,150);IncidentEnd=Button("结束已识别 Claude",Ink,false,170);IncidentDismiss=Button("关闭提示，保持冻结",Ink,false,190);
        incidentButtons.Controls.AddRange(new Control[]{IncidentRecover,IncidentEnd,IncidentDismiss});
        inner.Controls.Add(IncidentText,0,0);inner.Controls.Add(IncidentAction,0,1);inner.Controls.Add(incidentButtons,0,2);
        Incident.Controls.Add(inner);root.Controls.Add(Incident,0,1);
        var state=new Card{Dock=DockStyle.Fill};StateTitle=Label("正在读取保护状态",17,FontStyle.Bold,new Rectangle(22,17,620,33),Ink);StateDetail=Label("",10,FontStyle.Regular,new Rectangle(23,57,785,25),Muted);Processes=Label("",10,FontStyle.Regular,new Rectangle(690,20,150,30),Muted);Processes.TextAlign=ContentAlignment.MiddleRight;
        state.Controls.AddRange(new Control[]{StateTitle,StateDetail,Processes});root.Controls.Add(state,0,2);
        var pair=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        var left=new Card{Dock=DockStyle.Fill,Margin=new Padding(0,0,7,14)};left.Controls.Add(Label("固定节点",10,FontStyle.Regular,new Rectangle(18,14,180,23),Muted));Node=Label("读取中",15,FontStyle.Bold,new Rectangle(18,45,382,32),Ink);Expected=Label("预期 IP：—",11,FontStyle.Regular,new Rectangle(19,87,382,30),Ink);SelectNode=Button("选择节点…",Accent,false,110);SelectNode.SetBounds(300,8,110,32);left.Controls.AddRange(new Control[]{Node,Expected,SelectNode});pair.Controls.Add(left,0,0);
        var right=new Card{Dock=DockStyle.Fill,Margin=new Padding(7,0,0,14)};right.Controls.Add(Label("出口验证",10,FontStyle.Regular,new Rectangle(18,14,220,23),Muted));Observed=Label("尚未进行本次实测",13,FontStyle.Bold,new Rectangle(18,40,387,32),Ink);LastCheck=Label("历史通过时间：—",9,FontStyle.Regular,new Rectangle(19,81,390,37),Muted);right.Controls.AddRange(new Control[]{Observed,LastCheck});pair.Controls.Add(right,1,0);root.Controls.Add(pair,0,3);
        var mode=new Card{Dock=DockStyle.Fill};mode.Controls.Add(Label("遇到异常时",10,FontStyle.Bold,new Rectangle(18,13,170,27),Ink));Confirm=new RadioButton{Text="先冻结，确认后结束",Bounds=new Rectangle(184,12,245,29),Checked=true};Auto=new RadioButton{Text="直接结束全部 Claude",Bounds=new Rectangle(475,12,274,29)};mode.Controls.AddRange(new Control[]{Confirm,Auto});mode.Controls.Add(Label("关闭提示会保持冻结；恢复需要复检。自动结束会中断正在进行的任务。",9,FontStyle.Regular,new Rectangle(20,57,808,24),Muted));root.Controls.Add(mode,0,4);
        var verification=new Card{Dock=DockStyle.Fill};verification.Controls.Add(Label("验证结果",10,FontStyle.Bold,new Rectangle(18,13,180,25),Ink));Inspection=Label("点击下方「验证出口」查询两个来源的当前 IP。\n此操作仅检测，不解除锁定或恢复被冻结的进程。",10,FontStyle.Regular,new Rectangle(19,45,800,64),Muted);verification.Controls.Add(Inspection);root.Controls.Add(verification,0,5);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,Margin=new Padding(0),WrapContents=false};Verify=Button("验证出口",Accent,true,130);Resume=Button("复检并恢复",Ink,false,138);Alert=Button("故障详情",Ink,false,118);Logs=Button("运行日志",Ink,false,116);Code=Button("启动 Code",Ink,false,118);Desktop=Button("启动桌面",Ink,false,118);buttons.Controls.AddRange(new Control[]{Verify,Resume,Alert,Logs,Code,Desktop});root.Controls.Add(buttons,0,6);
        root.Controls.Add(Label("窗口关闭后继续在托盘保护。节点固定，不会自动切换；实际 IP 与预期 IP 必须一致。",9,FontStyle.Regular,new Rectangle(0,0,850,35),Muted),0,7);
        FormClosing+=(s,e)=>{if(e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
    }
    static Label Label(string text,float size,FontStyle style,Rectangle bounds,Color color){return new Label{Text=text,Font=new Font("Microsoft YaHei UI",size,style),Bounds=bounds,ForeColor=color,AutoSize=false};}
    static Button Button(string text,Color color,bool primary,int width){var b=new Button{Text=text,Width=width,Height=42,Margin=new Padding(0,0,10,0),FlatStyle=FlatStyle.Flat,ForeColor=primary?Color.White:color,BackColor=primary?color:Color.White,Cursor=Cursors.Hand};b.FlatAppearance.BorderColor=primary?color:Color.FromArgb(215,220,226);return b;}
    public void SetState(string phase,string reason,string last,int held,string node,string expected){
        StateTitle.Text=phase=="READY"?"保护正常，固定出口已验证":phase=="STARTING"?"正在验证出口":"已锁定，等待处理";
        StateTitle.ForeColor=phase=="READY"?Accent:Color.FromArgb(150,88,41);
        StateDetail.Text=reason;Processes.Text="冻结 "+held+" 个进程";Node.Text=node;Expected.Text="预期 IP："+expected;
        DateTime at;LastCheck.Text="历史通过时间："+(DateTime.TryParse(last,out at)?at.ToLocalTime().ToString("MM-dd HH:mm:ss"):"暂无")+"\n历史结果不代表当前网络已恢复";
    }
    public void ShowIncident(string text){IncidentText.Text=text;if(IncidentVisible)return;IncidentVisible=true;incidentStyle.Height=210;Incident.Visible=true;ClientSize=new Size(ClientSize.Width,ClientSize.Height+224);Refresh();}
    public void HideIncident(){if(!IncidentVisible)return;IncidentVisible=false;Incident.Visible=false;incidentStyle.Height=0;ClientSize=new Size(ClientSize.Width,ClientSize.Height-224);}
}
