using System.Buffers.Binary;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class Esp32S3FoundationTests
{
    const string Program="""
        #define R(a) (*(volatile uint32_t*)(a))
        volatile unsigned values[]={3,5,7}; volatile unsigned result;
        void print(unsigned port,const char *s){while(*s)R(port)=*s++;}
        extern "C" void classroom_main(){
          result=values[0]*11+values[1]*13+values[2]*17;
          print(0x60000000,"real-xtensa:");
          const char *hex="0123456789ABCDEF";
          R(0x60000000)=hex[(result>>4)&15]; R(0x60000000)=hex[result&15];
          print(0x60000000,";привет 😀\n");print(0x60010000,"independent-uart1\n");
        }
        """;
    [Fact]
    public async Task RealXtensaExecutesInitializedRamArithmeticAndTwoSeparateUarts()
    {
        Assert.True(Esp32S3Compiler.Available);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var elf=await Esp32S3Compiler.CompileBareMetal(Program,limit.Token);Esp32S3Image.Validate(elf);
        var result=await Esp32S3Foundation.Run(elf,limit.Token);Assert.True(result.ExitCode==0,result.Diagnostics);Assert.Equal("real-xtensa:D9;привет 😀\n",result.Uart0);Assert.Equal("independent-uart1\n",result.Uart1);
    }
    [Fact]
    public async Task ElfValidationRejectsForeignArchitectureBadBoundsAndMemoryMappedIoSegments()
    {
        var elf=await Esp32S3Compiler.CompileBareMetal(Program,CancellationToken.None);
        byte[] Change(int at,uint value){var copy=elf.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(at,4),value);return copy;}
        var foreign=elf.ToArray();BinaryPrimitives.WriteUInt16LittleEndian(foreign.AsSpan(18,2),40);Assert.Throws<InvalidDataException>(()=>Esp32S3Image.Validate(foreign));
        Assert.Throws<InvalidDataException>(()=>Esp32S3Image.Validate(Change(28,uint.MaxValue)));Assert.Throws<InvalidDataException>(()=>Esp32S3Image.Validate(Change(32,uint.MaxValue)));Assert.Throws<InvalidDataException>(()=>Esp32S3Image.Validate(Change(24,0x60000000)));
        var ph=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(elf.AsSpan(28,4)));Assert.Throws<InvalidDataException>(()=>Esp32S3Image.Validate(Change(ph+20,uint.MaxValue)));
        var mmio=Change(ph+8,0x60000000);BinaryPrimitives.WriteUInt32LittleEndian(mmio.AsSpan(ph+12,4),0x60000000);Assert.Throws<InvalidDataException>(()=>Esp32S3Image.Validate(mmio));
        Assert.Throws<InvalidDataException>(()=>Esp32S3Image.Validate(elf[..70]));Assert.Throws<InvalidDataException>(()=>Esp32S3Image.Validate(Change(ph+24,7)));
    }
    [Fact]
    public async Task CancelledGuestStopsAndNextGuestCanRun()
    {
        var loop=await Esp32S3Compiler.CompileBareMetal("extern \"C\" void classroom_main(){while(true)asm volatile(\"nop\");}",CancellationToken.None);
        using(var limit=new CancellationTokenSource(TimeSpan.FromSeconds(2)))await Assert.ThrowsAsync<IOException>(()=>Esp32S3Foundation.Run(loop,limit.Token));
        var good=await Esp32S3Compiler.CompileBareMetal(Program,CancellationToken.None);Assert.Equal(0,(await Esp32S3Foundation.Run(good,CancellationToken.None)).ExitCode);
    }
    [Fact]
    public async Task CompilerCannotIncludePrivateHostFiles()
    {
        var folder=Path.Combine(Path.GetTempPath(),"classroom-esp-private-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var file=Path.Combine(folder,"private.h");var canary="PRIVATE_CANARY_"+Guid.NewGuid().ToString("N");await File.WriteAllTextAsync(file,"#error "+canary);
        try{var error=await Assert.ThrowsAsync<InvalidDataException>(()=>Esp32S3Compiler.CompileBareMetal("#include \""+file.Replace('\\','/')+"\"\nextern \"C\" void classroom_main(){}",CancellationToken.None));Assert.DoesNotContain(canary,error.Message);Assert.Contains("private.h",error.Message);}
        finally{File.Delete(file);Directory.Delete(folder);}
    }
    [Fact]
    public async Task GuestSemihostingCannotOpenPrivateHostFiles()
    {
        var folder=Path.Combine(Path.GetTempPath(),"classroom-esp-guest-private-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var file=Path.Combine(folder,"private.txt");await File.WriteAllTextAsync(file,"Synthetic private guest probe");
        try{
            var path=System.Text.Json.JsonSerializer.Serialize(file);
            var code=$$"""
                extern "C" void classroom_main(){
                  const char *path={{path}};
                  register unsigned a2 asm("a2")=5;
                  register unsigned a3 asm("a3")=(unsigned)path;
                  register unsigned a4 asm("a4")=0;
                  register unsigned a5 asm("a5")=0;
                  asm volatile("simcall":"+r"(a2),"+r"(a3),"+r"(a4),"+r"(a5)::"memory");
                  const char *s=(int)a2<0?"DENIED\n":"HOST_FILE_OPENED\n";
                  while(*s)*(volatile unsigned*)0x60000000=*s++;
                }
                """;
            var image=await Esp32S3Compiler.CompileBareMetal(code,CancellationToken.None);var result=await Esp32S3Foundation.Run(image,CancellationToken.None);Assert.True(result.ExitCode==0,result.Diagnostics);Assert.Equal("DENIED\n",result.Uart0);
        }finally{File.Delete(file);Directory.Delete(folder);}
    }
    [Fact]
    public async Task ExcessiveUartOutputIsRejectedAndProcessIsRetired()
    {
        var image=await Esp32S3Compiler.CompileBareMetal("extern \"C\" void classroom_main(){for(;;)*(volatile unsigned*)0x60010000='x';}",CancellationToken.None);
        using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));var error=await Assert.ThrowsAsync<InvalidDataException>(()=>Esp32S3Foundation.Run(image,limit.Token));Assert.Contains("UART",error.Message);
    }
}
