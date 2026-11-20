using System.Text.Json;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class PicoScienceTests
{
    [Fact]
    public async Task EveryIndependentScientificVectorRunsThroughRealArmRegistersIncludingSincos()
    {
        using var fixture=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","pico-science.json")));
        var slots=new Dictionary<string,int>{{"cos",15},{"sin",16},{"tan",17},{"sincos",18},{"exp",19},{"log",20},{"atan2",22}};
        static string Hex(string value)=>"0x"+(value.TrimStart('0') is {Length:>0} trimmed?trimmed:"0")+"ULL";
        var rows=fixture.RootElement.GetProperty("vectors").EnumerateArray().Select(v=>{
            var results=v.GetProperty("results").EnumerateArray().Select(x=>x.GetString()!).ToArray();
            return "{"+string.Join(',',Hex(v.GetProperty("a").GetString()!),Hex(v.GetProperty("b").GetString()!),Hex(results[0]),Hex(results.Length==2?results[1]:"0"),v.GetProperty("width").GetInt32(),slots[v.GetProperty("operation").GetString()!],results.Length)+"}";
        });
        var code="""
            #include <stdint.h>
            typedef uintptr_t(*lookup_t)(uintptr_t,uint32_t);
            uintptr_t table(char c){return ((lookup_t)(uintptr_t)*(volatile uint16_t*)0x18)(*(volatile uint16_t*)0x16,'S'|((uint32_t)c<<8));}
            struct Case{uint64_t a,b,first,second;uint32_t width,slot,count;};
            const Case cases[]={
            """+string.Join(",\n",rows)+"""
            };
            bool check(const Case& c){
              uint32_t* functions=(uint32_t*)table(c.width==32?'F':'D');
              register uint32_t r0 asm("r0")=c.a;
              register uint32_t r1 asm("r1")=c.width==32?c.b:c.a>>32;
              register uint32_t r2 asm("r2")=c.b;
              register uint32_t r3 asm("r3")=c.b>>32;
              register uint32_t target asm("r4")=functions[c.slot];
              asm volatile("blx r4" : "+r"(r0),"+r"(r1),"+r"(r2),"+r"(r3) : "r"(target) : "r12","lr","cc","memory");
              uint64_t first=c.width==32?r0:(uint64_t)r0|((uint64_t)r1<<32);
              uint64_t second=c.width==32?r1:(uint64_t)r2|((uint64_t)r3<<32);
              return first==c.first&&(c.count==1||second==c.second);
            }
            extern "C" void reset();
            __attribute__((section(".vectors"),used)) const uintptr_t vectors[]={0x20042000,(uintptr_t)reset};
            extern "C" void reset(){
              *(volatile uint32_t*)0x40034030=0x301;*(volatile uint32_t*)0x4003402c=0x70;
              bool ok=true;for(const auto& c:cases){if(!check(c)){ok=false;break;}}
              const char* text=ok?"science:ok":"science:wrong";while(*text)*(volatile uint32_t*)0x40034000=*text++;
              while(1){}
            }
            """;
        Assert.InRange(code.Length,1,100000);
        var image=await PicoCompiler.Compile(code,CancellationToken.None,PicoSourceKind.BareMetal);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var output="";for(var i=0;i<20;i++){output+=(await runner.Step(milliseconds:1)).SerialPorts[1];if(output.Length>0)break;}
        Assert.Equal("science:ok",output);
    }
    [Fact]
    public async Task ArduinoMathFunctionsRunThroughSdkAndPrintRealResults()
    {
        const string code="""
            #include <math.h>
            #include <pico/double.h>
            void setup(){
              Serial1.begin(115200);volatile double x=.5,y=2;
              Serial1.print(sin(x),6);Serial1.print(';');Serial1.print(cos(x),6);Serial1.print(';');
              Serial1.print(tan(x),6);Serial1.print(';');Serial1.print(exp(x),6);Serial1.print(';');
              Serial1.print(log(y),6);Serial1.print(';');Serial1.print(atan2(x,y),6);
              volatile double large=1e20;volatile float largeFloat=1e10f;
              Serial1.print(';');Serial1.print(sin(large),6);Serial1.print(';');Serial1.print(cos(large),6);Serial1.print(';');Serial1.print(tan(large),6);
              Serial1.print(';');Serial1.print(sinf(largeFloat),6);Serial1.print(';');Serial1.print(cosf(largeFloat),6);Serial1.print(';');Serial1.print(tanf(largeFloat),6);
              double s,c;sincos(x,&s,&c);Serial1.print(';');Serial1.print(s,6);Serial1.print(';');Serial1.println(c,6);
            }
            void loop(){}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);var output="";
        for(var i=0;i<50;i++){output+=(await runner.Step(milliseconds:10)).SerialPorts[1];if(output.Contains('\n'))break;}
        using var fixture=JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"fixtures","pico-science.json")));
        var largeExpected=string.Join(';',fixture.RootElement.GetProperty("sdk_range_reduction").EnumerateArray().Select(v=>v.GetProperty("printed").GetString()));
        Assert.Equal("0.479426;0.877583;0.546302;1.648721;0.693147;0.244979;"+largeExpected+";0.479426;0.877583\r\n",output);
    }
}
