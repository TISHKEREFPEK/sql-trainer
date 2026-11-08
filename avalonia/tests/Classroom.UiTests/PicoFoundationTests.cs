using System.Text;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class PicoFoundationTests
{
    const string Bare="""
        #include <stdint.h>
        #define R(a) (*(volatile uint32_t *)(a))
        extern "C" void reset();
        __attribute__((section(".vectors"),used)) const uintptr_t vectors[]={0x20042000,(uintptr_t)reset};
        extern "C" void reset() {
          R(0x40014014)=5; R(0x4001c00c)=0x40;
          R(0x400140cc)=5; R(0xd0000024)=1u<<25;
          R(0x40034030)=0x301; R(0x40038030)=0x301;
          R(0x4003402c)=0x70; R(0x4003802c)=0x70;
          const char *s="real-arm:"; while(*s)R(0x40034000)=*s++;
          R(0x4004c000)=5; while(!(R(0x4004c000)&(1u<<8))){}
          uint32_t value=R(0x4004c004); const char *hex="0123456789ABCDEF";
          R(0x40034000)=hex[(value>>8)&15]; R(0x40034000)=hex[(value>>4)&15]; R(0x40034000)=hex[value&15];
          while(1) {
            if(R(0xd0000004)&4)R(0xd0000014)=1u<<25;else R(0xd0000018)=1u<<25;
            if(!(R(0x40034018)&16))R(0x40038000)=R(0x40034000);
          }
        }
        """;
    [Fact]
    public async Task CompiledArmInstructionsDriveGpioAdcAndIndependentUarts()
    {
        Assert.True(PicoCompiler.Available);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var image=await PicoCompiler.Compile(Bare,limit.Token,PicoSourceKind.BareMetal);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var pins=new bool[30];pins[2]=true;var frame=await runner.Step(pins,[1.65],milliseconds:1);
        Assert.True(frame.Pins[25].Output);Assert.InRange(frame.Pins[25].Voltage,3.2,3.3);Assert.StartsWith("real-arm:",frame.SerialPorts[1]);Assert.Empty(frame.SerialPorts[0]);Assert.Empty(frame.SerialPorts[2]);
        var adc=Convert.ToInt32(frame.SerialPorts[1]["real-arm:".Length..],16);Assert.InRange(adc,2047,2048);Assert.True(frame.Instructions>0);Assert.True(frame.Complete);
        frame=await runner.Step(pins,serialInput:"проверка 😀\n",serialPort:1,milliseconds:1);Assert.Equal("проверка 😀\n",frame.SerialPorts[2]);Assert.Empty(frame.SerialPorts[1]);
        pins[2]=false;frame=await runner.Step(pins,milliseconds:1);Assert.InRange(frame.Pins[25].Voltage,0,0.01);
        await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Step(analog:[5]));frame=await runner.Step(pins,milliseconds:1);Assert.True(frame.Complete);
        var fresh=new PicoFirmwareRunner();using(fresh){await fresh.Start(image);Assert.Equal("real-arm:000",(await fresh.Step(milliseconds:1)).SerialPorts[1]);}
    }
    [Fact]
    public async Task Uf2RejectsForeignDuplicateOverlappingMissingAndOutOfRangeBlocks()
    {
        byte[] binary=new byte[512];BitConverter.GetBytes(0x20042000u).CopyTo(binary,0);BitConverter.GetBytes(0x10000009u).CopyTo(binary,4);binary[8]=0xfe;binary[9]=0xe7;
        var valid=PicoCompiler.ToUf2(binary);
        async Task Reject(Action<byte[]> mutate)
        {var bad=valid.ToArray();mutate(bad);using var runner=new PicoFirmwareRunner();await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Start(bad));}
        await Reject(b=>BitConverter.GetBytes(0u).CopyTo(b,28));
        await Reject(b=>BitConverter.GetBytes(0u).CopyTo(b,512+20));
        await Reject(b=>BitConverter.GetBytes(0x10000080u).CopyTo(b,512+12));
        await Reject(b=>BitConverter.GetBytes(0x10200000u).CopyTo(b,512+12));
        await Reject(b=>BitConverter.GetBytes(3u).CopyTo(b,24));
        await Reject(b=>BitConverter.GetBytes(0x20000000u).CopyTo(b,32));
        using var good=new PicoFirmwareRunner();await good.Start(valid);Assert.True((await good.Step(milliseconds:1)).Instructions>0);
        var bounded=await good.Step(milliseconds:50);Assert.False(bounded.Complete);Assert.Equal(1000000,bounded.Instructions);
        Assert.True((await good.Step(milliseconds:1)).Complete);
    }
    [Theory]
    [InlineData(PicoSourceKind.BareMetal)]
    [InlineData(PicoSourceKind.Arduino)]
    public async Task CompilerCannotIncludeAPrivateFileOutsideItsGrantedFolders(PicoSourceKind kind)
    {
        var file=Path.Combine(Path.GetTempPath(),"classroom-pico-private-"+Guid.NewGuid().ToString("N")+".h");
        const string marker="PICO_PRIVATE_SENTINEL_MUST_NOT_BE_READ";await File.WriteAllTextAsync(file,"#error "+marker+"\n");
        try
        {
            Assert.Contains(marker,await File.ReadAllTextAsync(file));
            var source="#include \""+file.Replace('\\','/')+"\"\n"+Bare;
            var error=await Assert.ThrowsAsync<InvalidDataException>(()=>PicoCompiler.Compile(source,CancellationToken.None,kind));
            Assert.DoesNotContain(marker,error.Message);Assert.True(error.Message.Contains("Permission denied",StringComparison.OrdinalIgnoreCase)||error.Message.Contains("No such file",StringComparison.OrdinalIgnoreCase),error.Message);
        }
        finally{File.Delete(file);}
    }
    [Fact]
    public async Task ArduinoCoreCompilesOfflineInTheContainerAndRomDependencyFailsExplicitly()
    {
        using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var image=await PicoCompiler.Compile("void setup(){pinMode(25,OUTPUT);digitalWrite(25,HIGH);} void loop(){}",limit.Token);
        using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var failure=await Assert.ThrowsAsync<InvalidDataException>(async()=>{for(var i=0;i<100;i++)await runner.Step(milliseconds:1);});
        Assert.Contains("BootROM ABI",failure.Message);
    }
    [Fact]
    public async Task ArduinoCoreCacheUsesTheActualRuntimeAliasWhenAnotherDriveIsOccupied()
    {
        if(!OperatingSystem.IsWindows())return;
        var directory=Path.Combine(Path.GetTempPath(),"classroom-pico-alias-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            using var lease=new CompilerLease(directory,directory);using var reserved=CompilerDrive.Create(directory,lease);
            var image=await PicoCompiler.Compile("void setup(){} void loop(){}",CancellationToken.None);
            using var runner=new PicoFirmwareRunner();await runner.Start(image);
        }
        finally{Directory.Delete(directory);}
    }
}
