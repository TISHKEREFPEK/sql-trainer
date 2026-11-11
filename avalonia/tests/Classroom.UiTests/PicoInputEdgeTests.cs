using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class PicoInputEdgeTests
{
    [Fact]
    public async Task RepeatedHostSamplesDoNotInventInterruptsAndOneTransitionRaisesOne()
    {
        const string code="""
            #include <hardware/structs/iobank0.h>
            volatile unsigned int edges=0;
            void edge(){edges++;}
            void sample(){Serial1.print(edges);Serial1.print(':');Serial1.print((iobank0_hw->intr[0]>>28)&3);}
            void setup(){Serial1.begin(115200);pinMode(7,INPUT);attachInterrupt(digitalPinToInterrupt(7),edge,CHANGE);delay(100);sample();Serial1.print(';');delay(100);sample();Serial1.println(";DONE");}
            void loop(){}
            """;
        var image=await PicoCompiler.Compile(code,CancellationToken.None);using var runner=new PicoFirmwareRunner();await runner.Start(image);var inputs=new bool?[30];inputs[7]=false;var output="";
        for(var i=0;i<70&&!output.Contains("DONE");i++){output+=(await runner.Step(milliseconds:10,gpioInputs:inputs)).SerialPorts[1];if(output.Contains(';'))inputs[7]=true;}
        Assert.Equal("0:1;1:2;DONE\r\n",output);
    }
}
