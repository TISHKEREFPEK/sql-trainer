namespace Classroom.Desktop.Electronics;

// Worker bindings, independent of project/catalog identifiers. Native graph
// mapping and saved component support are a separate integration layer.
public sealed record SpiSignal(int? Pin=null,bool? Level=null);
public sealed record SpiDisplayDevice(string Id,int Controller,int Mosi,int Sck,int? Miso,SpiSignal? Cs,SpiSignal? Dc,SpiSignal? Rst,SpiSignal? Led,bool Powered=true,bool Connected=true,string Kind="ili9341");
public sealed record TftDisplayFrame(string Id,int Width,int Height,string Pixels,bool Display);
