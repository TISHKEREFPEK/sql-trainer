using Classroom.Desktop.Electronics;
using Classroom.Contracts;
using Classroom.Desktop;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using System.Reflection;
using Xunit;

namespace Classroom.UiTests;

public sealed class PicoWatchdogTests
{
    readonly Xunit.Abstractions.ITestOutputHelper output;
    public PicoWatchdogTests(Xunit.Abstractions.ITestOutputHelper output)=>this.output=output;
    [Fact]
    public async Task TwentyRebootsPreserveTheCounterWithinTheWorkerMemoryBudget()
    {
        const string code="""
            #include <hardware/watchdog.h>
            #include <hardware/structs/watchdog.h>
            void setup(){Serial1.begin(115200);if(++watchdog_hw->scratch[0]>20){Serial1.println("DONE");return;}watchdog_enable(1,false);}
            void loop(){}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var text="";var resets=0;long peak=0;
        var process=(System.Diagnostics.Process)typeof(PicoFirmwareRunner).GetField("process",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(runner)!;
        for(var i=0;i<180&&!text.Contains("DONE");i++){
            var frame=await runner.Step(milliseconds:10);text+=frame.SerialPorts[1];if(frame.ResetReason is not null)resets++;
            process.Refresh();peak=Math.Max(peak,process.PeakWorkingSet64);Assert.Empty(frame.Diagnostics);
        }
        output.WriteLine("Twenty warm resets; worker peak working set: "+peak+" bytes.");
        Assert.Contains("DONE",text);Assert.Equal(20,resets);Assert.InRange(peak,1,512L*1024*1024);
    }
    [AvaloniaFact]
    public async Task NativeWatchdogEventIsDurableAndVisibleInTheTeachersHistory()
    {
        await using var f=await LiveClassroom.Start();var directory=Path.Combine(Path.GetTempPath(),"classroom-watchdog-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var project=PicoNativeTests.Project();project.Code="""
            #include <hardware/watchdog.h>
            void setup(){Serial1.begin(115200);if(watchdog_caused_reboot()){Serial1.println("DONE");return;}watchdog_enable(10,false);}
            void loop(){}
            """;
        var file=Path.Combine(directory,"project.json");File.WriteAllText(file,project.Serialize());
        var owner=new MainWindow(false,false){Width=1320,Height=900};using var view=new ElectronicsScreen(owner,file,f.Student);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        try{
            owner.Preview(view);owner.Show();owner.UpdateLayout();
            await (Task)typeof(ElectronicsScreen).GetMethod("Compile",flags)!.Invoke(view,[true])!;
            ((DispatcherTimer)typeof(ElectronicsScreen).GetField("timer",flags)!.GetValue(view)!).Stop();
            Assert.True((bool)typeof(ElectronicsScreen).GetField("running",flags)!.GetValue(view)!);
            for(var i=0;i<70&&!view.LocalHistory!.Changes.Any(c=>c.SerialIo?.Text.Contains("DONE")==true);i++)await (Task)typeof(ElectronicsScreen).GetMethod("Tick",flags)!.Invoke(view,null)!;
            Assert.Single(view.LocalHistory!.Changes,c=>c.Kind=="watchdog-timer-reset");
            Assert.Contains(view.LocalHistory.Changes,c=>c.SerialIo?.Text.Contains("DONE")==true);view.Suspend();
            ElectronicsJournalInfo[] streams=[];
            for(var i=0;i<30;i++){await view.Synchronize();streams=await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}");if(streams.Any(s=>s.Id==view.LocalHistory.StreamId))break;await Task.Delay(50);}
            var stream=Assert.Single(streams,s=>s.Id==view.LocalHistory.StreamId);var events=await f.Admin.Get<ElectronicsJournalEvents>($"teacher/electronics-journal/{f.StudentId}/{stream.Id}/changes");
            Assert.Single(events.Events,c=>c.Change.Kind=="watchdog-timer-reset");Assert.Equal(project.Serialize(),File.ReadAllText(file));
        }finally{view.Suspend();owner.Close();}
    }
    static async Task<(string Text,List<PicoFrame> Frames)> Run(string code,PicoSourceKind kind=PicoSourceKind.Arduino,PicoI2cDevice[]? devices=null)
    {
        var image=await PicoCompiler.Compile(code,CancellationToken.None,kind);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var text="";var frames=new List<PicoFrame>();
        for(var i=0;i<120&&!text.Contains("DONE");i++){var frame=await runner.Step(milliseconds:10,devices:devices);text+=frame.SerialPorts[1];frames.Add(frame);Assert.Empty(frame.Diagnostics);}
        return (text,frames);
    }
    [Fact]
    public async Task SdkFeedsThenRebootsAndPreservesReasonScratchAndActualNorBytes()
    {
        const string code="""
            #include <hardware/watchdog.h>
            #include <hardware/structs/watchdog.h>
            #include <hardware/flash.h>
            void setup(){
              Serial1.begin(115200);
              if(watchdog_caused_reboot()){
                bool ok=watchdog_enable_caused_reboot()&&watchdog_hw->reason==1&&watchdog_hw->scratch[0]==0x12345678;
                const volatile byte* stored=(const volatile byte*)0x101ff000;for(int i=0;i<256;i++)ok&=stored[i]==(i^0xa5);
                Serial1.println(ok?"REBOOT:DONE":"WRONG:DONE");return;
              }
              byte page[256];for(int i=0;i<256;i++)page[i]=i^0xa5;
              noInterrupts();flash_range_erase(0x1ff000,4096);flash_range_program(0x1ff000,page,256);interrupts();
              watchdog_hw->scratch[0]=0x12345678;Serial1.println("BOOT");watchdog_enable(20,false);
              for(int i=0;i<3;i++){delay(10);watchdog_update();}Serial1.println("FED");while(true){}
            }
            void loop(){}
            """;
        var result=await Run(code);Assert.Equal("BOOT\r\nFED\r\nREBOOT:DONE\r\n",result.Text);
        Assert.Equal(1,Assert.Single(result.Frames,f=>f.ResetReason is not null).ResetReason);
        Assert.True(result.Frames.Zip(result.Frames.Skip(1),(a,b)=>b.Milliseconds>=a.Milliseconds).All(v=>v));
        Assert.False(Assert.Single(result.Frames,f=>f.ResetReason is not null).UsbReady);
    }
    [Fact]
    public async Task ForcedSdkRebootUsesTheOneShotPcStackHandoffWithoutReinitializingRam()
    {
        const string code="""
            #include <hardware/watchdog.h>
            #include <hardware/structs/watchdog.h>
            volatile unsigned token;
            __attribute__((noinline)) void __not_in_flash_func(target)(){
              *(volatile uint32_t*)0x40034030=0x301;*(volatile uint32_t*)0x4003402c=0x70;
              const uintptr_t location=(uintptr_t)&target;
              const char* text=token==0x76543210&&watchdog_hw->reason==2&&watchdog_hw->scratch[4]==0&&location>=0x20000000&&location<0x20042000?"TARGET:DONE":"WRONG:DONE";
              while(*text)*(volatile uint32_t*)0x40034000=*text++;while(true){}
            }
            void setup(){token=0x76543210;watchdog_reboot((uint32_t)(uintptr_t)&target,0x20041000,0);while(true){}}
            void loop(){}
            """;
        var result=await Run(code);Assert.Equal("TARGET:DONE",result.Text);Assert.Equal(2,Assert.Single(result.Frames,f=>f.ResetReason is not null).ResetReason);
    }
    [Fact]
    public async Task WatchdogRebootKeepsPoweredLcdAndOledMemory()
    {
        var code="#include <hardware/watchdog.h>\n"+OledTests.Project(mixed:true).Code.Replace("Serial.","Serial1.")
            .Replace("void setup() {","void setup() { Serial1.begin(115200);if(watchdog_caused_reboot()){Serial1.println(\"DONE\");while(true){}}")
            .Replace("Wire.begin();","Wire.setSDA(4);Wire.setSCL(5);Wire.begin();")
            .Replace("}\n\nvoid loop() {","watchdog_enable(20,false);}\n\nvoid loop() {");
        var result=await Run(code,devices:[new("lcd",39,16,2,true,true),new("oled",60,128,64,true,true,"ssd1306")]);
        Assert.Contains("DONE",result.Text);Assert.Single(result.Frames,f=>f.ResetReason is not null);
        var final=result.Frames[^1];Assert.Equal("LCD ready",new string(Assert.Single(final.Displays).Cells.Take(9).Select(v=>(char)v).ToArray()));
        var oled=Assert.Single(final.OledDisplays);Assert.True(oled.Display);Assert.True(OledTests.Pixel(oled,0,0));Assert.True(OledTests.Pixel(oled,127,63));
    }
    [Fact]
    public async Task BareMetalWarmResetPreservesRamButRestartsTheHardwareTimer()
    {
        const string code="""
            #include <stdint.h>
            #define R(a) (*(volatile uint32_t*)(a))
            extern "C" void reset();
            __attribute__((section(".vectors"),used)) const uintptr_t vectors[]={0x20042000,(uintptr_t)reset};
            void say(const char* s){while(*s)R(0x40034000)=*s++;}
            extern "C" void reset(){
              R(0x40034030)=0x301;R(0x4003402c)=0x70;
              if(R(0x40058008)==1){say(R(0x2003f000)==0xabcdef12&&R(0x40054028)<1000?"WARM:DONE":"WRONG:DONE");while(1){}}
              R(0x2003f000)=0xabcdef12;while(R(0x40054028)<10000){}
              R(0x40008030)=2;R(0x40008034)=0x100;R(0x4005802c)=0x20c;R(0x40010008)=0x1fffc;
              R(0x40058004)=4000;R(0x40058000)=0x40000000;while(1){}
            }
            """;
        var result=await Run(code,PicoSourceKind.BareMetal);Assert.Equal("WARM:DONE",result.Text);Assert.Single(result.Frames,f=>f.ResetReason==1);
        Assert.True(result.Frames[^1].Milliseconds>=12);
    }
}
