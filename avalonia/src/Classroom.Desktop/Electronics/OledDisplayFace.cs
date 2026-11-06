using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace Classroom.Desktop.Electronics;

public sealed class OledDisplayFace : Control
{
    readonly CircuitPart part;
    readonly OledFrame? frame;
    WriteableBitmap? bitmap;
    public OledDisplayFace(CircuitPart part,OledFrame? frame=null)
    {
        this.part=part;this.frame=frame;var size=PartArtwork.BaseBody(part);Width=size.Width;Height=size.Height;IsHitTestVisible=false;
        RenderOptions.SetBitmapInterpolationMode(this,BitmapInterpolationMode.None);
        DetachedFromVisualTree+=(_,_)=>{bitmap?.Dispose();bitmap=null;};
    }
    public override void Render(DrawingContext context)
    {
        var size=PartArtwork.BaseBody(part);
        context.DrawRectangle(Brush.Parse("#174965"),new Pen(Brush.Parse("#10354C"),2),new Rect(0,0,size.Width,size.Height),5,5);
        foreach(var p in new[]{new Point(10,10),new Point(size.Width-10,10),new Point(10,size.Height-10),new Point(size.Width-10,size.Height-10)})context.DrawEllipse(Brush.Parse("#C6C8BB"),new Pen(Brush.Parse("#142D3D"),2),p,4,4);
        context.DrawRectangle(Brush.Parse("#010306"),null,new Rect(12,48,196,108),2,2);
        if(frame is not null)
        {
            if(bitmap is null)
            {
                var pixels=Convert.FromBase64String(frame.Pixels);if(frame.Width!=128||frame.Height!=64||pixels.Length!=1024)throw new InvalidDataException("Некорректный кадр OLED.");
                bitmap=new WriteableBitmap(new PixelSize(128,64),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Opaque);
                using var target=bitmap.Lock();var row=new byte[128*4];var bright=(byte)(40+215*frame.Contrast/255);
                for(var y=0;y<64;y++)
                {
                    for(var x=0;x<128;x++){var on=(pixels[(y>>3)*128+x]&(1<<(y&7)))!=0;row[x*4]=on?bright:(byte)6;row[x*4+1]=on?bright:(byte)3;row[x*4+2]=on?(byte)(bright*.75):(byte)1;row[x*4+3]=255;}
                    Marshal.Copy(row,0,target.Address+y*target.RowBytes,row.Length);
                }
            }
            context.DrawImage(bitmap,new Rect(0,0,128,64),new Rect(14,54,192,96));
        }
        context.DrawText(new FormattedText($"SSD1306 · 128×64 · 0x{(int)part.Value:X2}",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,Typeface.Default,10,Brushes.White),new Point(15,174));
    }
}
