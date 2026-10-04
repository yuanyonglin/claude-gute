using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class BrandAssets {
    static GraphicsPath Rounded(RectangleF r,float d){var p=new GraphicsPath();p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
    public static void Main(string[] args){
        int[] sizes={16,32,48,64,128,256};var frames=new byte[sizes.Length][];
        for(int i=0;i<sizes.Length;i++)using(var b=new Bitmap(sizes[i],sizes[i]))using(var g=Graphics.FromImage(b)){
            g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(sizes[i]/256f,sizes[i]/256f);
            using(var box=Rounded(new RectangleF(8,8,240,240),60))using(var bg=new LinearGradientBrush(new Point(0,0),new Point(256,256),Color.FromArgb(32,46,65),Color.FromArgb(15,23,35)))g.FillPath(bg,box);
            var shield=new GraphicsPath();shield.AddLines(new[]{new PointF(128,42),new PointF(202,70),new PointF(196,137)});shield.AddBezier(196,137,189,177,152,203,128,216);shield.AddBezier(128,216,103,203,66,177,60,137);shield.AddLine(60,137,54,70);shield.CloseFigure();
            using(var pen=new Pen(Color.FromArgb(225,145,112),12)){pen.LineJoin=LineJoin.Round;g.DrawPath(pen,shield);}shield.Dispose();
            using(var pen=new Pen(Color.FromArgb(250,245,240),17)){pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;g.DrawArc(pen,91,89,74,74,43,274);}
            using(var mark=new SolidBrush(Color.FromArgb(106,206,173)))g.FillEllipse(mark,167,170,42,42);
            using(var pen=new Pen(Color.FromArgb(20,47,42),5)){g.DrawLines(pen,new[]{new Point(178,190),new Point(185,197),new Point(199,183)});}
            using(var ms=new MemoryStream()){b.Save(ms,ImageFormat.Png);frames[i]=ms.ToArray();}
            if(sizes[i]==256)b.Save(Path.ChangeExtension(args[0],".png"),ImageFormat.Png);
        }
        using(var w=new BinaryWriter(File.Create(args[0]))){w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)sizes.Length);int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++){w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)(sizes[i]==256?0:sizes[i]));w.Write((byte)0);w.Write((byte)0);w.Write((ushort)1);w.Write((ushort)32);w.Write(frames[i].Length);w.Write(offset);offset+=frames[i].Length;}
            foreach(var f in frames)w.Write(f);
        }
    }
}
