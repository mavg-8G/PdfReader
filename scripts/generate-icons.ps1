# Rebuild Folio's Windows icons from vector paths. Uses only Windows/.NET drawing APIs.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'PdfReader/Assets'
$previewDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/icons'
[IO.Directory]::CreateDirectory($previewDir) | Out-Null
$drawingReferences = @('System.Drawing.Common', 'System.Drawing.Primitives', 'System.Collections')
foreach ($optionalAssembly in @('System.Private.Windows.Core', 'System.Private.Windows.GdiPlus')) {
    try { $drawingReferences += [Reflection.Assembly]::Load($optionalAssembly).Location } catch { }
}
Add-Type -ReferencedAssemblies $drawingReferences -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections.Generic;

public static class FolioIcons
{
    // One vector definition drives both the editable SVG master and raster exports.
    sealed class Shape : IDisposable
    {
        public GraphicsPath Path = new GraphicsPath();
        public StringBuilder Svg = new StringBuilder();
        PointF point;
        public Shape M(float x, float y) { point = new PointF(x,y); Path.StartFigure(); Svg.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,"M{0} {1}",x,y); return this; }
        public Shape L(float x, float y) { Path.AddLine(point, new PointF(x,y)); point = new PointF(x,y); Svg.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,"L{0} {1}",x,y); return this; }
        public Shape C(float a,float b,float c,float d,float x,float y) { Path.AddBezier(point,new PointF(a,b),new PointF(c,d),new PointF(x,y)); point=new PointF(x,y); Svg.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,"C{0} {1} {2} {3} {4} {5}",a,b,c,d,x,y); return this; }
        public Shape Z() { Path.CloseFigure(); Svg.Append("Z"); return this; }
        public void Dispose() { Path.Dispose(); }
    }
    static Shape Back() { return new Shape().M(43,60).L(164,43).C(172,42,177,47,178,55).L(200,211).C(201,219,196,225,188,226).L(67,243).C(59,244,53,239,52,231).L(30,75).C(29,67,35,61,43,60).Z(); }
    static Shape Paper() { return new Shape().M(74,18).L(158,18).L(218,78).L(218,212).C(218,222,211,229,201,229).L(74,229).C(64,229,57,222,57,212).L(57,35).C(57,25,64,18,74,18).Z(); }
    static Shape Fold() { return new Shape().M(158,18).L(218,78).L(176,78).C(166,78,158,70,158,60).Z(); }
    static Shape Letter() { return new Shape().M(87,91).L(177,91).L(177,111).L(108,111).L(108,136).L(159,136).L(159,156).L(108,156).L(108,196).L(87,196).Z(); }
    static Color ColorOf(string hex) { return ColorTranslator.FromHtml(hex); }
    static void Mark(Graphics g, float x, float y, float size, bool mono)
    {
        var state=g.Save(); g.TranslateTransform(x,y); g.ScaleTransform(size/256f,size/256f);
        using(var paper=Paper()) using(var letter=Letter()) using(var fold=Fold()) using(var back=Back())
        {
            if(mono)
            {
                // Lock-screen badge: white glyph with genuinely transparent F and fold.
                using(var region=new Region(paper.Path)) using(var white=new SolidBrush(Color.White))
                { region.Exclude(letter.Path); region.Exclude(fold.Path); g.FillRegion(white,region); }
            }
            else
            {
                using(var brush=new SolidBrush(ColorOf("#AEC7FF"))) g.FillPath(brush,back.Path);
                using(var gradient=new LinearGradientBrush(new PointF(57,18),new PointF(218,229),ColorOf("#657AE3"),ColorOf("#3C4EAD"))) g.FillPath(gradient,paper.Path);
                using(var brush=new SolidBrush(ColorOf("#D9E6FF"))) g.FillPath(brush,fold.Path);
                using(var brush=new SolidBrush(Color.White)) g.FillPath(brush,letter.Path);
            }
        }
        g.Restore(state);
    }
    public static Bitmap Render(int width,int height,float markSize,bool mono)
    {
        const int supersample=4;
        using(var large=new Bitmap(width*supersample,height*supersample,PixelFormat.Format32bppArgb))
        {
            using(var g=Graphics.FromImage(large))
            { g.Clear(Color.Transparent); g.SmoothingMode=SmoothingMode.AntiAlias; g.ScaleTransform(supersample,supersample); Mark(g,(width-markSize)/2,(height-markSize)/2,markSize,mono); }
            var result=new Bitmap(width,height,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(result))
            { g.CompositingMode=CompositingMode.SourceCopy; g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.PixelOffsetMode=PixelOffsetMode.HighQuality; g.DrawImage(large,new Rectangle(0,0,width,height),0,0,large.Width,large.Height,GraphicsUnit.Pixel); }
            return result;
        }
    }
    static void Save(string folder,string name,int width,int height,float markSize,bool mono=false)
    { using(var bitmap=Render(width,height,markSize,mono)) bitmap.Save(System.IO.Path.Combine(folder,name),ImageFormat.Png); }
    public static void Generate(string folder,string preview)
    {
        Save(folder,"LockScreenLogo.scale-200.png",48,48,46,true);
        Save(folder,"SplashScreen.scale-200.png",1240,600,240);
        Save(folder,"Square150x150Logo.scale-200.png",300,300,248);
        Save(folder,"Square44x44Logo.scale-200.png",88,88,86);
        Save(folder,"Square44x44Logo.targetsize-24_altform-unplated.png",24,24,24);
        Save(folder,"StoreLogo.png",50,50,48);
        Save(folder,"Wide310x150Logo.scale-200.png",620,300,224);
        var sizes=new int[]{16,20,24,32,40,48,64,128,256};
        var frames=new List<byte[]>();
        foreach(var size in sizes)
        { using(var bitmap=Render(size,size,size,false)) using(var stream=new MemoryStream()) { bitmap.Save(stream,ImageFormat.Png); frames.Add(stream.ToArray()); } }
        using(var writer=new BinaryWriter(File.Create(System.IO.Path.Combine(folder,"Folio.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            uint offset=(uint)(6+sizes.Length*16);
            for(int i=0;i<sizes.Length;i++)
            { writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((uint)frames[i].Length); writer.Write(offset); offset+=(uint)frames[i].Length; }
            foreach(var frame in frames) writer.Write(frame);
        }
        using(var back=Back()) using(var paper=Paper()) using(var fold=Fold()) using(var letter=Letter())
        {
            string svg="<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 256 256\"><title>Folio — folded document and reading mark</title><defs><linearGradient id=\"paper\" gradientUnits=\"userSpaceOnUse\" x1=\"57\" y1=\"18\" x2=\"218\" y2=\"229\"><stop stop-color=\"#657AE3\"/><stop offset=\"1\" stop-color=\"#3C4EAD\"/></linearGradient></defs><path fill=\"#AEC7FF\" d=\""+back.Svg+"\"/><path fill=\"url(#paper)\" d=\""+paper.Svg+"\"/><path fill=\"#D9E6FF\" d=\""+fold.Svg+"\"/><path fill=\"white\" d=\""+letter.Svg+"\"/></svg>";
            File.WriteAllText(System.IO.Path.Combine(folder,"Folio.svg"),svg);
        }
        // A review sheet, kept out of the app's shipped Assets directory.
        using(var sheet=new Bitmap(960,550)) using(var g=Graphics.FromImage(sheet))
        using(var labelFont=new Font("Segoe UI",12)) using(var titleFont=new Font("Segoe UI",23,FontStyle.Bold))
        using(var white=new SolidBrush(Color.White)) using(var ink=new SolidBrush(ColorOf("#24304B")))
        {
            g.Clear(ColorOf("#F0F3FA")); g.SmoothingMode=SmoothingMode.AntiAlias;
            using(var dark=new SolidBrush(ColorOf("#172031"))) g.FillRectangle(dark,480,0,480,550);
            g.DrawString("Folio",titleFont,ink,32,24); g.DrawString("Folio",titleFont,white,512,24);
            using(var big=Render(240,240,240,false)) { g.DrawImageUnscaled(big,120,82); g.DrawImageUnscaled(big,600,82); }
            int[] displayed={16,24,32,48,64}; int position=38;
            foreach(int size in displayed)
            {
                using(var icon=Render(size,size,size,false)) { g.DrawImageUnscaled(icon,position,386-size/2); g.DrawImageUnscaled(icon,position+480,386-size/2); }
                g.DrawString(size+" px",labelFont,ink,position-3,436); g.DrawString(size+" px",labelFont,white,position+477,436);
                position+=88;
            }
            g.DrawString("Light backgrounds",labelFont,ink,32,505); g.DrawString("Dark backgrounds",labelFont,white,512,505);
            sheet.Save(System.IO.Path.Combine(preview,"folio-icons-preview.png"),ImageFormat.Png);
        }
    }
}
'@
[FolioIcons]::Generate($assetDir, $previewDir)
Get-ChildItem -LiteralPath $assetDir | Select-Object Name, Length
