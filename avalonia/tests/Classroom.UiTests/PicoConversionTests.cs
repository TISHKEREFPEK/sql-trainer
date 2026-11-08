using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class PicoConversionTests
{
    [Fact]
    public async Task ArduinoAnalogWriteDrivesRealPwmWhileDelayKeepsHardwareTimerRunning()
    {
        const string code="""
            void setup(){pinMode(25,OUTPUT);analogWriteFreq(1000);analogWriteRange(255);analogWrite(15,64);}
            void loop(){digitalWrite(25,HIGH);delay(5);digitalWrite(25,LOW);delay(5);}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);PicoFrame? frame=null;
        for(var i=0;i<20;i++)frame=await runner.Step(milliseconds:10);
        Assert.NotNull(frame);Assert.True(frame.Pins[15].Output);Assert.InRange(frame.Pins[15].Voltage,0.72,0.94);
        Assert.InRange(frame.Pins[15].Frequency!.Value,995,1005);Assert.InRange(frame.Pins[25].Frequency!.Value,95,105);Assert.Empty(frame.Diagnostics);
    }
    [Fact]
    public async Task ArmCallsPreserveFixedPointRoundingSaturationWidthAndRegisterAlignment()
    {
        const string code="""
            #include <stdint.h>
            typedef uintptr_t (*lookup_t)(uintptr_t,uint32_t);
            uintptr_t table(char second){return ((lookup_t)(uintptr_t)*(volatile uint16_t*)0x18)(*(volatile uint16_t*)0x16,'S'|((uint32_t)second<<8));}
            typedef int32_t(*fsi)(float);typedef int64_t(*fsl)(float);typedef int64_t(*fsfix)(float,int);
            typedef int64_t(*dsl)(double);typedef int64_t(*dsfix)(double,int);typedef uint64_t(*dufix)(double,int);
            typedef float(*lsf)(int64_t,int);typedef double(*lsd)(int64_t,int);typedef float(*dsf)(double);typedef double(*fsd)(float);
            typedef int(*cmpf)(float,float);typedef int(*cmpd)(double,double);
            extern "C" void reset();
            __attribute__((section(".vectors"),used)) const uintptr_t vectors[]={0x20042000,(uintptr_t)reset};
            extern "C" void reset(){
              *(volatile uint32_t*)0x40034030=0x301;*(volatile uint32_t*)0x4003402c=0x70;
              uint32_t* f=(uint32_t*)table('F');uint32_t* d=(uint32_t*)table('D');bool ok=true;
              ok&=((fsi)f[7])(-1.5f)==-2;ok&=((fsl)f[27])(-1.5f)==-2;
              ok&=((fsfix)f[28])(0.5f,16)==32768;ok&=((dsfix)d[28])(-0.5,16)==-32768;
              ok&=((dsfix)d[28])(1.0,0x7fffffff)==INT64_MAX;ok&=((dsfix)d[28])(-1.0,0x80000000)==-1;
              ok&=((dufix)d[30])(1.0,64)==UINT64_MAX;ok&=((dsl)d[27])(-1.75)==-2;
              union{float f;uint32_t u;} a;a.f=((lsf)f[24])(33554435,1);ok&=a.u==0x4b800001;
              union{double d;uint64_t u;} b;b.d=((lsd)d[24])(9007199254740995LL,1);ok&=b.u==0x4330000000000002ULL;
              a.f=((lsf)f[24])(1,0x80000000);ok&=a.u==0x7f800000;
              b.d=((lsd)d[24])(-1,0x7fffffff);ok&=b.u==0x8000000000000000ULL;
              a.f=((dsf)d[31])(1.5);ok&=a.u==0x3fc00000;b.d=((fsd)f[31])(-0.5f);ok&=b.u==0xbfe0000000000000ULL;
              ok&=((cmpf)f[21])(-0.0f,0.0f)==0;ok&=((cmpf)f[21])(-2,1)==-1;ok&=((cmpd)d[21])(2,-1)==1;
              const char* text=ok?"convert:ok":"convert:wrong";while(*text)*(volatile uint32_t*)0x40034000=*text++;
              while(1){}
            }
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None,PicoSourceKind.BareMetal);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        Assert.Equal("convert:ok",(await runner.Step(milliseconds:1)).SerialPorts[1]);
    }
}
