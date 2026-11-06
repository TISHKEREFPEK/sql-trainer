using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class AvrBoardTests
{
    [Theory]
    [InlineData("uno")]
    [InlineData("mega2560")]
    public async Task RealBoardCoreDrivesPortsTimersAdcEepromAndEveryUsart(string id)
    {
        var board=AvrBoards.Get(id);var mega=id=="mega2560";var pin=mega?53:13;var pwm=mega?44:9;var adc=mega?15:5;
        var channels=mega?4:1;
        var code="#include <EEPROM.h>\n"+(mega?"#ifndef __AVR_ATmega2560__":"#ifndef __AVR_ATmega328P__")+"\n#error Wrong CPU\n#endif\n"+
            "void setup(){"+string.Join("",Enumerable.Range(0,channels).Select(n=>$"Serial{(n==0?"":n.ToString())}.begin(9600);"))+
            $"pinMode({pin},OUTPUT);pinMode({pwm},OUTPUT);analogWrite({pwm},64);EEPROM.update({(mega?3000:500)},42);"+
            $"Serial.print(\"E:\");Serial.println(EEPROM.read({(mega?3000:500)}));}}\n"+
            $"void loop(){{digitalWrite({pin},HIGH);"+string.Join("",Enumerable.Range(0,channels).Select(n=>$"Serial{(n==0?"":n.ToString())}.println(\"U{n}\");"))+
            $"Serial.print(\"ADC:\");Serial.println(analogRead(A{adc}));delay(100);digitalWrite({pin},LOW);delay(100);}}";
        var hex=await FirmwareRunner.Compile(code,CancellationToken.None,id);using var runner=new FirmwareRunner();await runner.Start(hex,id);
        var input=new bool[board.DigitalCount];var analog=new double[board.AnalogCount];analog[adc]=3;
        var serial=Enumerable.Range(0,channels).Select(_=>"").ToArray();var highs=0;var lows=0;var voltages=new List<double>();var frequencies=new List<double>();
        for(var i=0;i<12;i++)
        {
            var frame=await runner.Step(input,analog);Assert.Equal(board.DigitalCount+board.AnalogCount,frame.Pins.Length);Assert.Equal(channels,frame.SerialPorts!.Length);
            for(var n=0;n<channels;n++)serial[n]+=frame.SerialPorts[n];if(frame.Pins[pin].Voltage>4)highs++;if(frame.Pins[pin].Voltage<1)lows++;
            if(i>0){voltages.Add(frame.Pins[pwm].Voltage);if(frame.Pins[pwm].Frequency is { }freq)frequencies.Add(freq);}
        }
        Assert.True(highs>0&&lows>0);Assert.Contains("E:42",serial[0]);Assert.Contains("ADC:614",serial[0]);
        for(var i=0;i<channels;i++)Assert.Contains("U"+i,serial[i]);Assert.InRange(voltages.Average(),1.1,1.4);Assert.All(frequencies,f=>Assert.InRange(f,480,510));Assert.NotEmpty(frequencies);
    }

    [Fact]
    public async Task MegaLoadsLargeFirmwareAndReadsFarFlashAndUnoRejectsIt()
    {
        var arrays=string.Join("\n",Enumerable.Range(0,8).Select(i=>$"const unsigned char data{i}[20000] PROGMEM_FAR={{{i+1}}};"));
        var reads=string.Join("",Enumerable.Range(0,8).Select(i=>$"address=pgm_get_far_address(data{i});sum+=pgm_read_byte_far(address);if(address>131072UL)high=1;"));
        var code="#include <avr/pgmspace.h>\n"+arrays+"\nvoid setup(){Serial.begin(9600);unsigned int sum=0;unsigned long address=0;int high=0;"+reads+"Serial.print(\"FAR:\");Serial.print(sum);Serial.print(':');Serial.println(high);}\nvoid loop(){}";
        var hex=await FirmwareRunner.Compile(code,CancellationToken.None,"mega2560");Assert.True(hex.Length>300000);
        using var runner=new FirmwareRunner();await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Start(hex));await runner.Start(hex,"mega2560");
        var text="";for(var i=0;i<5;i++)text+=(await runner.Step(new bool[54],new double[16])).Serial;Assert.Contains("FAR:36:1",text);
    }

    [Fact]
    public async Task MegaCallsAndReturnsFromCodeAbove128KiB()
    {
        // Capture the high byte of an actual three-byte return address. A C/C++
        // function symbol address may use word addressing and is not this proof.
        var code="extern \"C\" unsigned char far_probe();volatile unsigned char far_pc=0;\n"+
            "asm(\".pushsection .text.far_probe,\\\"ax\\\",@progbits\\n.global far_probe\\n.type far_probe, @function\\n.space 140000\\nfar_probe:\\nrcall .+0\\npop r18\\npop r19\\npop r20\\nsts far_pc,r18\\nldi r24,42\\nret\\n.popsection\\n\");\n"+
            "void setup(){Serial.begin(9600);Serial.print(\"HIGHPC:\");Serial.print(far_probe());Serial.print(':');Serial.println(far_pc);}\nvoid loop(){}";
        var hex=await FirmwareRunner.Compile(code,CancellationToken.None,"mega2560");using var runner=new FirmwareRunner();await runner.Start(hex,"mega2560");
        var text="";for(var i=0;i<4;i++)text+=(await runner.Step(new bool[54],new double[16])).Serial;Assert.Contains("HIGHPC:42:1",text);
    }

    [Fact]
    public async Task MegaUsartReceivesOnlyItsOwnInputAndPreservesUtf8()
    {
        var code="void setup(){Serial.begin(9600);Serial1.begin(9600);Serial2.begin(9600);Serial3.begin(9600);}\nvoid loop(){if(Serial.available())Serial.write(Serial.read());if(Serial1.available())Serial1.write(Serial1.read());if(Serial2.available())Serial2.write(Serial2.read());if(Serial3.available())Serial3.write(Serial3.read());}";
        var hex=await FirmwareRunner.Compile(code,CancellationToken.None,"mega2560");using var runner=new FirmwareRunner();await runner.Start(hex,"mega2560");await runner.Step(new bool[54],new double[16]);
        var text="порт-2: abc";var output=new string[4];Array.Fill(output,"");
        for(var i=0;i<5;i++){var frame=await runner.Step(new bool[54],new double[16],i==0?text:null,2);for(var n=0;n<4;n++)output[n]+=frame.SerialPorts![n];}
        Assert.Equal(text,output[2]);Assert.Equal("",output[0]);Assert.Equal("",output[1]);Assert.Equal("",output[3]);
        await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Step(new bool[54],new double[16],"x",4));
        await Assert.ThrowsAsync<InvalidDataException>(()=>runner.Step(new bool[54],new double[16],new string('x',4097),2));
        Assert.Equal(70,(await runner.Step(new bool[54],new double[16])).Pins.Length);
    }

    [Fact]
    public async Task MegaExternalAndPinChangeInterruptsUseTheirOwnVectors()
    {
        var code="#include <avr/interrupt.h>\nvoid edge(){digitalWrite(22,HIGH);}\nISR(PCINT2_vect){digitalWrite(53,HIGH);}\nvoid setup(){pinMode(22,OUTPUT);pinMode(53,OUTPUT);pinMode(18,INPUT);pinMode(A15,INPUT);attachInterrupt(digitalPinToInterrupt(18),edge,RISING);PCICR|=_BV(PCIE2);PCMSK2|=_BV(PCINT23);}\nvoid loop(){}";
        var hex=await FirmwareRunner.Compile(code,CancellationToken.None,"mega2560");using var runner=new FirmwareRunner();await runner.Start(hex,"mega2560");
        var digital=new bool[54];var analog=new double[16];var before=await runner.Step(digital,analog);Assert.InRange(before.Pins[22].Voltage,0,0.1);Assert.InRange(before.Pins[53].Voltage,0,0.1);
        digital[18]=true;analog[15]=5;var after=await runner.Step(digital,analog);Assert.InRange(after.Pins[22].Voltage,4.9,5);Assert.InRange(after.Pins[53].Voltage,4.9,5);
    }

    [Fact]
    public async Task MegaServoUsesTimer5WithoutUnoTimerAliases()
    {
        var hex=await FirmwareRunner.Compile("#include <Servo.h>\nServo servo;void setup(){servo.attach(44);servo.writeMicroseconds(1500);}\nvoid loop(){}",CancellationToken.None,"mega2560");
        using var runner=new FirmwareRunner();await runner.Start(hex,"mega2560");FirmwareFrame? frame=null;for(var i=0;i<4;i++)frame=await runner.Step(new bool[54],new double[16]);
        Assert.NotNull(frame!.Pins[44].PulseUs);Assert.InRange(frame.Pins[44].PulseUs!.Value,1480,1520);Assert.InRange(frame.Pins[44].Frequency!.Value,48,52);
    }

    [Theory]
    [InlineData("uno",13)]
    [InlineData("mega2560",53)]
    public async Task WatchdogInterruptHasTheCorrectVector(string id,int pin)
    {
        var code="#include <avr/wdt.h>\n#include <avr/interrupt.h>\n"+$"ISR(WDT_vect){{digitalWrite({pin},HIGH);wdt_disable();}}\nvoid setup(){{pinMode({pin},OUTPUT);cli();wdt_reset();WDTCSR=_BV(WDCE)|_BV(WDE);WDTCSR=_BV(WDIE);sei();}}\nvoid loop(){{}}";
        var hex=await FirmwareRunner.Compile(code,CancellationToken.None,id);using var runner=new FirmwareRunner();await runner.Start(hex,id);var board=AvrBoards.Get(id);
        await runner.Step(new bool[board.DigitalCount],new double[board.AnalogCount]);var frame=await runner.Step(new bool[board.DigitalCount],new double[board.AnalogCount]);Assert.InRange(frame.Pins[pin].Voltage,4.9,5);
    }
}
