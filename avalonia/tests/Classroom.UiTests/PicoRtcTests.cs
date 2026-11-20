using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class PicoRtcTests
{
    [Fact]
    public async Task ActualSdkCalendarAlarmIrqAndPausedClockExecuteOffline()
    {
        const string code="""
            #include <hardware/rtc.h>
            #include <hardware/structs/rtc.h>
            volatile int fired=0;
            void alarm(){fired++;}
            void show(){datetime_t t;rtc_get_datetime(&t);Serial1.print(t.year);Serial1.print(';');Serial1.print(t.month);Serial1.print(';');Serial1.print(t.day);Serial1.print(';');Serial1.print(t.dotw);Serial1.print(';');Serial1.println(t.sec);}
            void setup(){
              Serial1.begin(115200);rtc_init();datetime_t t={2024,2,28,4,23,59,59};rtc_set_datetime(&t);
              datetime_t a={2024,2,29,5,0,0,0};rtc_set_alarm(&a,alarm);
              rtc_hw->clkdiv_m1=468;delay(35);show();Serial1.println(fired);
              rtc_hw->ctrl=0;delay(30);Serial1.println((int)rtc_running());
              Serial1.println((int)(rtc_hw->rtc_0&63));rtc_hw->ctrl=1;delay(11);show();
              t={2100,2,28,1,23,59,59};rtc_set_datetime(&t);rtc_hw->ctrl|=256;delay(12);show();
              fired=0;t={2026,1,1,4,0,0,0};rtc_set_datetime(&t);a={-1,-1,-1,-1,-1,-1,1};rtc_set_alarm(&a,alarm);
              delay(25);Serial1.println(fired);rtc_set_datetime(&t);delay(25);Serial1.println(fired);
              Serial1.println("DONE");
            }
            void loop(){}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);
        var output="";var diagnostics=new List<string>();
        for(var i=0;i<80&&!output.Contains("DONE");i++){var frame=await runner.Step(milliseconds:10);output+=frame.SerialPorts[1];diagnostics.AddRange(frame.Diagnostics);}
        Assert.Equal("2024;2;29;5;2\r\n1\r\n0\r\n2\r\n2024;2;29;5;3\r\n2100;3;1;2;0\r\n1\r\n2\r\nDONE\r\n",output);
        Assert.Empty(diagnostics);
    }
}
