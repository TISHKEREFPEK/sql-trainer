using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class PicoRomTests
{
    [Theory]
    [InlineData(0,"ROM memory range")]
    [InlineData(1,"read-only through XIP")]
    [InlineData(2,"float/add")]
    public async Task InvalidMemoryAndUnsupportedRomCallsFailAndRequireReload(int scenario,string expected)
    {
        var code=$$"""
            #include <stdint.h>
            typedef uintptr_t (*lookup_t)(uintptr_t,uint32_t);
            uintptr_t lookup(int offset,char a,char b){return ((lookup_t)(uintptr_t)*(volatile uint16_t*)0x18)(*(volatile uint16_t*)(uintptr_t)offset,(uint32_t)a|((uint32_t)b<<8));}
            extern "C" void reset();
            __attribute__((section(".vectors"),used)) const uintptr_t vectors[]={0x20042000,(uintptr_t)reset};
            extern "C" void reset(){
              if({{scenario}}==0)((void(*)(uintptr_t,uint32_t,uint32_t))lookup(0x14,'M','S'))(0x20041ff0,0xaa,32);
              if({{scenario}}==1)*(volatile uint32_t*)0x10001000=0;
              if({{scenario}}==2){uintptr_t table=lookup(0x16,'S','F');((void(*)())*(uint32_t*)table)();}
              while(1){}
            }
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None,PicoSourceKind.BareMetal);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Step(milliseconds:1));Assert.Contains(expected,error.Message);
        var halted=await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Step(milliseconds:1));Assert.Contains("Load Pico firmware",halted.Message);
        var safe=new byte[256];BitConverter.GetBytes(0x20042000u).CopyTo(safe,0);BitConverter.GetBytes(0x10000009u).CopyTo(safe,4);safe[8]=0xfe;safe[9]=0xe7;
        await runner.Start(PicoCompiler.ToUf2(safe));Assert.True((await runner.Step(milliseconds:1)).Instructions>0);
    }
    [Fact]
    public async Task SdkFlashEraseAndProgramChangeActualNorBytesAndPreserveZeroBits()
    {
        const string code="""
            #include <hardware/flash.h>
            void setup(){
              Serial1.begin(115200);
              uint8_t page[256];for(int i=0;i<256;i++)page[i]=i^0xa5;
              noInterrupts();flash_range_erase(0x1ff000,4096);flash_range_program(0x1ff000,page,256);interrupts();
              const volatile uint8_t* stored=(const volatile uint8_t*)0x101ff000;
              bool ok=true;for(int i=0;i<256;i++)ok&=stored[i]==(i^0xa5);
              for(int i=0;i<256;i++)page[i]=255;
              noInterrupts();flash_range_program(0x1ff000,page,256);interrupts();
              for(int i=0;i<256;i++)ok&=stored[i]==(i^0xa5);
              noInterrupts();flash_range_erase(0x1ff000,4096);interrupts();
              for(int i=0;i<4096;i++)ok&=stored[i]==255;
              Serial1.println(ok?"flash:ok":"flash:wrong");
            }
            void loop(){}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);var output="";
        for(var i=0;i<50;i++){var frame=await runner.Step(milliseconds:10);output+=frame.SerialPorts[1];if(output.Contains('\n'))break;}
        Assert.Equal("flash:ok\r\n",output);
    }
    [Fact]
    public async Task UsbCdcEnumeratesThroughRealDescriptorsAndEchoesUtf8WithoutMixingUarts()
    {
        const string code="""
            void setup(){Serial.begin(115200);while(!Serial){}Serial.println("usb:ready");}
            void loop(){while(Serial.available())Serial.write(Serial.read());}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var output="";PicoFrame? frame=null;
        for(var i=0;i<100;i++){frame=await runner.Step(milliseconds:10);output+=frame.SerialPorts[0];Assert.Empty(frame.SerialPorts[1]);Assert.Empty(frame.SerialPorts[2]);if(output.Contains('\n'))break;}
        Assert.NotNull(frame);Assert.True(frame.UsbReady);Assert.Equal("usb:ready\r\n",output);output="";
        for(var i=0;i<30;i++){frame=await runner.Step(serialInput:i==0?"USB данные 😀\n":null,milliseconds:10);output+=frame.SerialPorts[0];if(output.Contains('\n'))break;}
        Assert.Equal("USB данные 😀\n",output);Assert.Empty(frame.SerialPorts[1]);Assert.Empty(frame.SerialPorts[2]);Assert.Empty(frame.Diagnostics);
        var bulk=string.Concat(Enumerable.Repeat("пакет 😀 0123456789\n",80));output="";
        for(var i=0;i<100;i++){frame=await runner.Step(serialInput:i==0?bulk:null,milliseconds:10);output+=frame.SerialPorts[0];if(output.Length>=bulk.Length)break;}
        Assert.Equal(bulk,output);Assert.Empty(frame.SerialPorts[1]);Assert.Empty(frame.SerialPorts[2]);Assert.Equal(0,frame.SerialPending[0]);
    }
    [Fact]
    public async Task RealArduinoSketchUsesAdcGpioUartsAndSerialFlashIdentity()
    {
        const string code="""
            #include <hardware/flash.h>
            void setup() {
              pinMode(2,INPUT); pinMode(25,OUTPUT); analogReadResolution(12);
              Serial1.setTX(0); Serial1.setRX(1); Serial1.begin(115200);
              Serial2.setTX(4); Serial2.setRX(5); Serial2.begin(115200);
              Serial1.print("adc:");Serial1.print(analogRead(26));Serial1.print(";uid:");
              uint8_t id[8];flash_get_unique_id(id);
              for(int i=0;i<8;i++){if(id[i]<16)Serial1.print('0');Serial1.print(id[i],HEX);}
              Serial1.println();
            }
            void loop() {
              digitalWrite(25,digitalRead(2));
              while(Serial1.available())Serial2.write(Serial1.read());
            }
            """;
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var image=await PicoCompiler.Compile(code,timeout.Token);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var pins=new bool[30];pins[2]=true;var output="";PicoFrame? frame=null;
        for(var i=0;i<40;i++){frame=await runner.Step(pins,[1.65],milliseconds:10);output+=frame.SerialPorts[1];if(output.Contains('\n'))break;}
        Assert.Equal("adc:2048;uid:53514C4300000001\r\n",output);
        frame=await runner.Step(pins,milliseconds:10);Assert.True(frame.Pins[25].Output);Assert.InRange(frame.Pins[25].Voltage,3.2,3.3);
        output="";
        for(var i=0;i<10;i++){frame=await runner.Step(pins,serialInput:i==0?"данные 😀\n":null,serialPort:1,milliseconds:10);output+=frame.SerialPorts[2];if(output.Contains('\n'))break;}
        Assert.Equal("данные 😀\n",output);pins[2]=false;await runner.Step(pins,milliseconds:10);frame=await runner.Step(pins,milliseconds:10);Assert.InRange(frame.Pins[25].Voltage,0,0.01);Assert.Empty(frame.Diagnostics);
    }
    [Fact]
    public async Task CompiledCodeUsesRomLookupMemoryAndBitFunctionsAndCannotChangeRom()
    {
        const string code="""
            #include <stdint.h>
            #define R(a) (*(volatile uint32_t *)(a))
            typedef uintptr_t (*lookup_t)(uintptr_t,uint32_t);
            uintptr_t lookup(char a,char b){return ((lookup_t)(uintptr_t)*(volatile uint16_t*)0x18)(*(volatile uint16_t*)0x14,(uint32_t)a|((uint32_t)b<<8));}
            typedef uint32_t (*bits_t)(uint32_t);
            typedef void* (*copy_t)(void*,const void*,uint32_t);
            typedef void* (*fill_t)(void*,uint32_t,uint32_t);
            extern "C" void reset();
            __attribute__((section(".vectors"),used)) const uintptr_t vectors[]={0x20042000,(uintptr_t)reset};
            extern "C" void reset(){
              R(0x40034030)=0x301;R(0x4003402c)=0x70;
              uint8_t a[36] __attribute__((aligned(4))),b[36] __attribute__((aligned(4)));
              bool ok=((fill_t)lookup('M','S'))(a+1,0xab,31)==a+1;
              ok&=((copy_t)lookup('M','C'))(b+1,a+1,31)==b+1;for(int i=1;i<32;i++)ok&=b[i]==0xab;
              ok&=((fill_t)lookup('S','4'))(a,0x5c,36)==a;
              ok&=((copy_t)lookup('C','4'))(b,a,36)==b;for(int i=0;i<36;i++)ok&=b[i]==0x5c;
              ok&=((fill_t)lookup('S','4'))(a,0x7a,35)==a;ok&=a[35]==0x5c;
              ok&=((copy_t)lookup('C','4'))(b,a,35)==b;for(int i=0;i<35;i++)ok&=b[i]==0x7a;ok&=b[35]==0x5c;
              ok&=((bits_t)lookup('L','3'))(0)==32;ok&=((bits_t)lookup('L','3'))(0x100)==23;
              ok&=((bits_t)lookup('T','3'))(0)==32;ok&=((bits_t)lookup('T','3'))(0x100)==8;
              ok&=((bits_t)lookup('P','3'))(0xf000000f)==8;ok&=((bits_t)lookup('R','3'))(0x80000101)==0x80800001;
              ok&=lookup('Z','Z')==0;uint32_t vector=R(0);R(0)=0xffffffff;ok&=R(0)==vector;
              const char *text=ok?"rom:ok":"rom:wrong";while(*text)R(0x40034000)=*text++;
              while(1){}
            }
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None,PicoSourceKind.BareMetal);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        Assert.Equal("rom:ok",(await runner.Step(milliseconds:1)).SerialPorts[1]);
    }
}
