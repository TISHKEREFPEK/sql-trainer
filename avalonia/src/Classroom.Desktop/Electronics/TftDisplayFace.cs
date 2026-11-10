using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace Classroom.Desktop.Electronics;

// Original vector artwork. Pin placement follows the audited nine-pin module;
// no Cirkit SVG/source is included. The pixel surface has the panel's 3:4 ratio.
public sealed class TftDisplayFace : Control
{
    public static readonly Size ModuleSize=new(240,400.5);
    public static readonly Rect Panel=new(15,90,210,280);
    public static Point Header(int index)=>new((502.90315+100*index)/1830.709*ModuleSize.Width,63.46929/3055.118*ModuleSize.Height);
    readonly TftDisplayFrame? frame;
    WriteableBitmap? bitmap;
    public TftDisplayFace(TftDisplayFrame? frame=null)
    {
        this.frame=frame;Width=ModuleSize.Width;Height=ModuleSize.Height;IsHitTestVisible=false;
        RenderOptions.SetBitmapInterpolationMode(this,BitmapInterpolationMode.None);
        DetachedFromVisualTree+=(_,_)=>{bitmap?.Dispose();bitmap=null;};
    }
    static void Text(DrawingContext context,string text,double size,IBrush brush,Point point)=>context.DrawText(new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,Typeface.Default,size,brush),point);
    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brush.Parse("#17639A"),new Pen(Brush.Parse("#103C61"),2),new Rect(ModuleSize),5,5);
        foreach(var p in new[]{new Point(12,15),new Point(228,15),new Point(12,385),new Point(228,385)})context.DrawEllipse(Brush.Parse("#C6C8BB"),new Pen(Brush.Parse("#123A51"),2),p,4,4);
        context.DrawRectangle(Brush.Parse("#101A20"),null,new Rect(55,0,130,20));
        var pins=PartLibrary.Pins(PartKind.TftSpi);
        for(var i=0;i<pins.Length;i++){
            var header=Header(i);context.DrawRectangle(Brush.Parse("#D3B563"),null,new Rect(header.X-2,header.Y-3,4,6));
            using(context.PushTransform(Matrix.CreateRotation(Math.PI/2)*Matrix.CreateTranslation(header.X+4,25)))Text(context,pins[i],9,Brushes.White,new(0,0));
        }
        context.DrawRectangle(Brush.Parse("#B8BFC4"),new Pen(Brush.Parse("#32414C"),1),new Rect(10,85,220,290),2,2);
        context.DrawRectangle(Brush.Parse("#03070A"),null,Panel);
        if(frame is {Display:true}){
            if(bitmap is null){
                var rgb=Convert.FromBase64String(frame.Pixels);if(frame.Width!=240||frame.Height!=320||rgb.Length!=240*320*3)throw new InvalidDataException("Некорректный кадр TFT.");
                bitmap=new WriteableBitmap(new PixelSize(240,320),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Opaque);
                using var target=bitmap.Lock();var row=new byte[240*4];
                for(var y=0;y<320;y++){
                    for(var x=0;x<240;x++){var source=(y*240+x)*3;var dest=x*4;row[dest]=rgb[source+2];row[dest+1]=rgb[source+1];row[dest+2]=rgb[source];row[dest+3]=255;}
                    Marshal.Copy(row,0,target.Address+y*target.RowBytes,row.Length);
                }
            }
            context.DrawImage(bitmap,new Rect(0,0,240,320),Panel);
        }
        Text(context,"ILI9341  ·  240 × 320  ·  SPI",11,Brushes.White,new(35,381));
    }
}
