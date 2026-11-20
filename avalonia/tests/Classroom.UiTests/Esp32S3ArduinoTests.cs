using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class Esp32S3ArduinoTests
{
    [Fact]
    public async Task OrdinarySketchBootsFromFlashAndRunsSetupAndLoop()
    {
        Assert.True(Esp32S3Compiler.ArduinoAvailable);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(7));
        var image=await Esp32S3Compiler.CompileArduino("""
            #include <Preferences.h>
            int ticks = 0;
            void setup() {
              Serial.begin(115200);
              Serial1.begin(115200, SERIAL_8N1, 18, 17);
              Serial.println("Arduino S3 setup");
              Preferences preferences;
              if (preferences.begin("classroom", false)) {
                preferences.putUInt("tries", 73);
                preferences.end();
                if (preferences.begin("classroom", true)) {
                  Serial.print("flash=");
                  Serial.println(preferences.getUInt("tries"));
                  preferences.end();
                }
              }
            }
            void loop() {
              if (++ticks <= 3) { Serial.print("loop="); Serial.println(ticks); Serial1.println("Arduino S3 UART1"); }
              delay(60);
            }
            """,limit.Token);
        Assert.Equal(0x7f,image.Elf[0]);Assert.Equal(0xe9,image.Bootloader[0]);Assert.Equal(0xe9,image.Application[0]);Assert.Equal(new byte[]{0xaa,0x50},image.Partitions[..2]);
        var run = await Esp32S3Foundation.RunArduino(image, TimeSpan.FromSeconds(8), limit.Token);
        Assert.True(run.Uart0.Contains("Arduino S3 setup") && run.Uart0.Contains("loop=3"),
            $"UART0:\n{run.Uart0}\nUART1:\n{run.Uart1}\nQEMU:\n{run.Diagnostics}");
        Assert.Contains("Arduino S3 UART1", run.Uart1);
        Assert.Contains("flash=73", run.Uart0);
        Assert.True(run.StoppedAtLimit);
        var damaged = image.Flash.ToArray();
        damaged[0x10000] = 0;
        Assert.Throws<InvalidDataException>(() =>
        {
            _ = Esp32S3Foundation.RunArduino(image with { Flash = damaged }, TimeSpan.FromSeconds(8), limit.Token);
        });
    }
}
