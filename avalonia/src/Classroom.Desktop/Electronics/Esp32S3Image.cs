using System.Buffers.Binary;

namespace Classroom.Desktop.Electronics;

public static class Esp32S3Image
{
    public static void Validate(byte[] image)
    {
        void Invalid()=>throw new InvalidDataException("Неверная ELF-прошивка основания ESP32-S3.");
        if(image is null||image.Length<84||image.Length>2*1024*1024){Invalid();return;}
        ushort U16(int at)=>BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at,2));
        uint U32(int at)=>BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at,4));
        if(!image.AsSpan(0,7).SequenceEqual(new byte[]{0x7f,69,76,70,1,1,1})||U16(16)!=2||U16(18)!=94||U32(20)!=1||U16(40)!=52||U16(42)!=32)Invalid();
        var offset=U32(28);var count=U16(44);var entry=U32(24);
        if(count is 0 or >16||(ulong)offset+(ulong)count*32>(ulong)image.Length||offset<52)Invalid();
        var sectionOffset=U32(32);var sections=U16(48);
        if(sections>1024||U16(46)!=40||(ulong)sectionOffset+(ulong)sections*40>(ulong)image.Length||sections>0&&(sectionOffset<52||U16(50)>=sections))Invalid();
        for(var i=0;i<sections;i++){
            var p=checked((int)sectionOffset+i*40);if(U32(p+4) is 0 or 8)continue;
            if((ulong)U32(p+16)+U32(p+20)>(ulong)image.Length)Invalid();
        }
        var executable=false;var ranges=new List<(uint Start,uint End)>();
        for(var i=0;i<count;i++){
            var p=checked((int)offset+i*32);var type=U32(p);if(type is 2 or 3)Invalid();if(type!=1)continue;
            var file=U32(p+4);var address=U32(p+8);var physical=U32(p+12);var size=U32(p+16);var memory=U32(p+20);var flags=U32(p+24);var end=(ulong)address+memory;
            var code=address>=0x40378000&&end<=0x40398000;var data=address>=0x3fc90000&&end<=0x3fcb0000;
            if(address!=physical||memory==0||size>memory||(ulong)file+size>(ulong)image.Length||(!code&&!data)||(flags&~7u)!=0||(flags&1)!=0&&!code||(flags&2)!=0&&!data||ranges.Any(r=>address<r.End&&end>r.Start))Invalid();
            if((flags&1)!=0&&entry>=address&&(ulong)entry<(ulong)address+size)executable=true;
            ranges.Add((address,(uint)end));
        }
        if(!executable)Invalid();
    }
}
