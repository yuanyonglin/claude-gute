using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Shared palette and painted controls for the dashboard and node picker.
public static class Theme {
    public static readonly Color Ground=Color.FromArgb(245,246,248), Surface=Color.White, Border=Color.FromArgb(227,230,235), Divider=Color.FromArgb(238,240,243);
    public static readonly Color Ink=Color.FromArgb(27,34,48), Body=Color.FromArgb(61,71,87), Muted=Color.FromArgb(91,101,117), Disabled=Color.FromArgb(140,149,163);
    public static readonly Color Link=Color.FromArgb(36,87,166), LinkTint=Color.FromArgb(232,238,248), Control=Color.FromArgb(213,218,225);
    public static readonly Color GoodTint=Color.FromArgb(230,242,236), Good=Color.FromArgb(31,122,85), GoodInk=Color.FromArgb(23,96,63);
    public static readonly Color WaitTint=Color.FromArgb(255,244,224), Wait=Color.FromArgb(178,106,0), WaitInk=Color.FromArgb(138,82,0);
    public static readonly Color BadTint=Color.FromArgb(251,234,230), Bad=Color.FromArgb(168,50,31), BadInk=Color.FromArgb(138,36,20), BadBorder=Color.FromArgb(226,185,176);
    public const string Sans="Microsoft YaHei UI", Mono="Consolas";
    public static Font Font(float size,FontStyle style=FontStyle.Regular){return new Font(Sans,size,style);}
    public static Label Text(string text,float size,Color color,FontStyle style=FontStyle.Regular){
        return new Label{Text=text,Font=Font(size,style),ForeColor=color,AutoSize=true,Margin=new Padding(0)};
    }
    public static GraphicsPath Round(RectangleF r,float radius){
        var p=new GraphicsPath();float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
        if(d<=0){p.AddRectangle(r);return p;}
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
}

// Rounded surface. Children sit on Fill; the corners show the parent's background.
public class RoundPanel : TableLayoutPanel {
    Color fill=Theme.Surface, border=Color.Empty;
    public int Radius=12;
    // BackColor mirrors Fill so labels and nested panels inherit it as their ambient background.
    public RoundPanel(){DoubleBuffered=true;ResizeRedraw=true;BackColor=fill;Padding=new Padding(18,14,18,14);Margin=new Padding(0,0,0,12);Dock=DockStyle.Fill;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;ColumnCount=1;}
    public Color Fill{get{return fill;}set{fill=value;BackColor=value;Invalidate();}}
    public Color Stroke{get{return border;}set{border=value;Invalidate();}}
    protected override void OnPaintBackground(PaintEventArgs e){
        using(var b=new SolidBrush(Parent!=null?Parent.BackColor:Theme.Ground))e.Graphics.FillRectangle(b,ClientRectangle);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var path=Theme.Round(new RectangleF(0.5f,0.5f,Width-1.5f,Height-1.5f),Radius*e.Graphics.DpiX/96f))
        using(var b=new SolidBrush(fill)){e.Graphics.FillPath(b,path);if(border!=Color.Empty)using(var pen=new Pen(border))e.Graphics.DrawPath(pen,path);}
    }
}

public enum ButtonKind { Primary, Secondary, Link }
// Painted button; still a real Button, so Click, PerformClick, Enabled and keyboard focus behave as before.
public class FlatBtn : Button {
    public ButtonKind Kind;
    Color accent=Theme.Ink, accentBorder=Theme.Control;
    bool hover, down;
    public FlatBtn(string text,ButtonKind kind,Color accent){
        Text=text;Kind=kind;this.accent=accent;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true);
        Font=Theme.Font(kind==ButtonKind.Link?9.5f:10f,kind==ButtonKind.Link?FontStyle.Regular:FontStyle.Bold);Cursor=Cursors.Hand;AutoSize=false;
        Height=kind==ButtonKind.Link?32:42;Margin=new Padding(0,0,10,0);Padding=new Padding(0);BackColor=Color.Transparent;
        Width=TextRenderer.MeasureText(text,Font).Width+(kind==ButtonKind.Link?12:36);
    }
    public Color Accent{get{return accent;}set{accent=value;Invalidate();}}
    public Color AccentBorder{get{return accentBorder;}set{accentBorder=value;Invalidate();}}
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;down=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnMouseDown(MouseEventArgs e){down=true;Invalidate();base.OnMouseDown(e);}
    protected override void OnMouseUp(MouseEventArgs e){down=false;Invalidate();base.OnMouseUp(e);}
    protected override void OnEnabledChanged(EventArgs e){Cursor=Enabled?Cursors.Hand:Cursors.Default;Invalidate();base.OnEnabledChanged(e);}
    protected override void OnPaint(PaintEventArgs e){
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        using(var b=new SolidBrush(Parent!=null?ParentColor(Parent):Theme.Ground))g.FillRectangle(b,ClientRectangle);
        Color fill=Color.Empty,stroke=Color.Empty,text;
        if(Kind==ButtonKind.Primary){fill=Enabled?(down?ControlPaint.Dark(accent,0.1f):hover?ControlPaint.Light(accent,0.15f):accent):Color.FromArgb(201,206,214);text=Color.White;}
        else if(Kind==ButtonKind.Secondary){fill=Enabled&&hover?Theme.Ground:Theme.Surface;stroke=Enabled?accentBorder:Theme.Border;text=Enabled?accent:Theme.Disabled;}
        else{text=Enabled?(hover?ControlPaint.Dark(accent,0.2f):accent):Theme.Disabled;}
        using(var path=Theme.Round(new RectangleF(0.5f,0.5f,Width-1.5f,Height-1.5f),9*e.Graphics.DpiX/96f)){
            if(fill!=Color.Empty)using(var b=new SolidBrush(fill))g.FillPath(b,path);
            if(stroke!=Color.Empty)using(var p=new Pen(stroke))g.DrawPath(p,path);
        }
        TextRenderer.DrawText(g,Text,Font,ClientRectangle,text,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
        if(Focused&&ShowFocusCues){var r=ClientRectangle;r.Inflate(-3,-3);ControlPaint.DrawFocusRectangle(g,r);}
    }
    static Color ParentColor(Control c){for(;c!=null;c=c.Parent)if(c.BackColor!=Color.Transparent)return c.BackColor;return Theme.Ground;}
}

// Small rounded label used for facts in the status banner.
public class Chip : Label {
    public Chip(){AutoSize=true;Font=Theme.Font(9.5f);ForeColor=Theme.Body;Padding=new Padding(8,4,8,4);Margin=new Padding(0,0,8,0);BackColor=Color.Transparent;}
    protected override void OnPaintBackground(PaintEventArgs e){
        using(var b=new SolidBrush(Parent!=null?Parent.BackColor:Theme.Ground))e.Graphics.FillRectangle(b,ClientRectangle);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(var path=Theme.Round(new RectangleF(0,0,Width-1,Height-1),6*e.Graphics.DpiX/96f))using(var b=new SolidBrush(Color.White))e.Graphics.FillPath(b,path);
    }
}

public enum StatusGlyph { Check, Lock, Pending }
// Filled circle with a white glyph; shape differs per state, not just color.
public class StatusIcon : Control {
    StatusGlyph glyph=StatusGlyph.Pending;Color color=Theme.Wait;
    public StatusIcon(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.SupportsTransparentBackColor,true);BackColor=Color.Transparent;Size=new Size(40,40);Margin=new Padding(0,0,14,0);}
    public void Set(StatusGlyph g,Color c){glyph=g;color=c;Invalidate();}
    protected override void OnPaint(PaintEventArgs e){
        var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        using(var b=new SolidBrush(Parent!=null?Parent.BackColor:Theme.Ground))g.FillRectangle(b,ClientRectangle);
        float s=Math.Min(Width,Height)/40f;
        using(var b=new SolidBrush(color))g.FillEllipse(b,0,0,40*s-1,40*s-1);
        using(var pen=new Pen(Color.White,2.6f*s){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}){
            if(glyph==StatusGlyph.Check)g.DrawLines(pen,new[]{new PointF(12*s,20.5f*s),new PointF(17.5f*s,26*s),new PointF(28*s,14.5f*s)});
            else if(glyph==StatusGlyph.Lock){g.DrawRectangle(pen,13*s,19*s,14*s,10*s);g.DrawArc(pen,15.5f*s,11*s,9*s,11*s,180,180);}
            else using(var w=new SolidBrush(Color.White))for(int i=0;i<3;i++)g.FillEllipse(w,(12.5f+i*6.5f)*s,18*s,4*s,4*s);
        }
    }
}
