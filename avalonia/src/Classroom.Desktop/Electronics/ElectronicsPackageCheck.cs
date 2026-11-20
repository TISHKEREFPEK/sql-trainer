using System.Security.Cryptography;
using System.Text.Json;

namespace Classroom.Desktop.Electronics;

// Fixed-source offline release check. No MainWindow, login, database, student
// project or user settings are opened. It must use this published app's SDKs.
public static class ElectronicsPackageCheck
{
    public static async Task<int> Run(string outputDirectory)
    {
        var output=Path.GetFullPath(outputDirectory);
        if(Directory.Exists(output)&&Directory.EnumerateFileSystemEntries(output).Any()){Console.Error.WriteLine("Package-check output must be a new or empty folder.");return 2;}
        Directory.CreateDirectory(output);
        // Native GNU tools can resolve virtual drive aliases back to their
        // physical prefix. Do not place their cache under a long report path.
        var cacheRoot=Path.Combine(Path.GetTempPath(),"classroom-package-check-"+Guid.NewGuid().ToString("N"));
        var cache=Path.Combine(cacheRoot,"compiler-cache");
        var checks=new List<object>();var passed=false;string? error=null;
        try{
            Environment.SetEnvironmentVariable("CLASSROOM_ELECTRONICS_RUNTIME",null);Environment.SetEnvironmentVariable("CLASSROOM_PICO_RUNTIME",null);
            Environment.SetEnvironmentVariable("CLASSROOM_ESP32S3_RUNTIME",null);
            Environment.SetEnvironmentVariable("CLASSROOM_ESP32S3_GPIO_RUNTIME",null);
            Environment.SetEnvironmentVariable("CLASSROOM_COMPILER_CACHE",cache);Environment.SetEnvironmentVariable("CLASSROOM_COMPILER_LEASES",Path.Combine(output,"leases"));
            var root=AppContext.BaseDirectory;
            foreach(var folder in new[]{"runtime","pico-runtime"})foreach(var file in new[]{"arduino-cli.exe","node.exe","ArduinoCLI-LICENSE.txt","Node-LICENSE.txt"})
                if(!File.Exists(Path.Combine(root,"electronics",folder,file)))throw new IOException("Published SDK file is missing: "+folder+"/"+file);
            if(Path.GetFullPath(FirmwareRunner.Runtime)!=Path.GetFullPath(Path.Combine(root,"electronics","runtime"))||Path.GetFullPath(PicoCompiler.Runtime)!=Path.GetFullPath(Path.Combine(root,"electronics","pico-runtime")))throw new IOException("Release check must use the SDKs next to this executable.");
            string? s3Runtime = null;
            if (Directory.Exists(Esp32S3Compiler.BundledRuntime) || Directory.Exists(Esp32S3Board.BundledRuntime))
            {
                s3Runtime = Esp32S3Board.ConfiguredRuntime;
                if (s3Runtime != Esp32S3Board.BundledRuntime || Esp32S3Compiler.Runtime != Esp32S3Compiler.BundledRuntime)
                    throw new IOException("Published ESP32-S3 requires the Arduino and GPIO runtimes next to this executable.");
            }
            foreach(var board in new[]{"uno","mega2560"}){
                var marker="published:"+board+":проверка";
                var code="void setup(){Serial.begin(115200);pinMode(13,OUTPUT);digitalWrite(13,HIGH);Serial.println(\""+marker+"\");}void loop(){}";
                var firmware=await FirmwareRunner.Compile(code,CancellationToken.None,board);using var runner=new FirmwareRunner();await runner.Start(firmware,board);var profile=AvrBoards.Get(board);var serial="";
                for(var i=0;i<12&&!serial.Contains(marker);i++)serial+=(await runner.Step(new bool[profile.DigitalCount],new double[profile.AnalogCount])).Serial;
                if(!serial.Contains(marker))throw new InvalidDataException("Packaged AVR did not execute its compiled sketch: "+board);
                checks.Add(new{board,compiled=true,executed=true,serial,firmwareSha256=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(firmware)))});
            }
            const string picoCode="""
                #include <Wire.h>
                #include <Adafruit_GFX.h>
                #include <Adafruit_SSD1306.h>
                Adafruit_SSD1306 screen(128,64,&Wire,-1);
                void setup(){Serial1.begin(115200);Wire.setSDA(4);Wire.setSCL(5);Wire.begin();if(!screen.begin(SSD1306_SWITCHCAPVCC,0x3c))return;screen.clearDisplay();screen.drawRect(0,0,128,64,SSD1306_WHITE);screen.display();Serial1.println("published:pico:проверка");}
                void loop(){}
                """;
            var image=await PicoCompiler.Compile(picoCode,CancellationToken.None);using(var runner=new PicoFirmwareRunner()){
                await runner.Start(image);var serial="";PicoFrame? frame=null;
                for(var i=0;i<100&&!serial.Contains("published:pico:проверка");i++){frame=await runner.Step(devices:[new("oled",60,128,64,true,true,"ssd1306")]);serial+=frame.SerialPorts[1];}
                if(!serial.Contains("published:pico:проверка")||frame?.OledDisplays.SingleOrDefault()?.Display!=true)throw new InvalidDataException("Packaged Pico did not execute its OLED sketch.");
                checks.Add(new{board="pico",compiled=true,executed=true,oled=true,serial,firmwareSha256=Convert.ToHexString(SHA256.HashData(image))});
            }
            if (s3Runtime is not null) checks.Add(await VerifyEsp32S3(s3Runtime));
            passed=true;
        }catch(Exception e){error=e.ToString();}
        finally{
            // This is only the newly owned verification cache, never a user's
            // normal compiler cache or the published read-only SDK itself.
            try{var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(!Path.GetFullPath(cacheRoot).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(cacheRoot).StartsWith("classroom-package-check-",StringComparison.Ordinal))throw new IOException("Invalid package-check cache path.");if(passed&&Directory.Exists(cacheRoot))Directory.Delete(cacheRoot,true);}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException){error=(error is null?"":error+"\n")+"Verification cache cleanup: "+e.Message;passed=false;}
        }
        var report=new{version=1,passed,application=Path.GetFileName(Environment.ProcessPath),baseDirectory=AppContext.BaseDirectory,diagnosticCache=passed?null:cacheRoot,desktopSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ElectronicsPackageCheck).Assembly.Location))),checks,error};
        await File.WriteAllTextAsync(Path.Combine(output,"package-check.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(passed?"Published electronics check passed.":"Published electronics check failed; see package-check.json.");return passed?0:1;
    }

    static async Task<object> VerifyEsp32S3(string runtime)
    {
        const string code = """
            #include <Wire.h>
            void setup() {
                Serial.begin(115200);
                pinMode(12, INPUT_PULLUP);
                pinMode(39, OUTPUT);
                if (!Wire.begin(8, 9, 100000)) return;
                const uint8_t init[] = {0x00, 0xae, 0x8d, 0x14, 0x20, 0x00, 0xa1, 0xc8, 0xaf};
                Wire.beginTransmission(0x3c);
                Wire.write(init, sizeof(init));
                if (Wire.endTransmission() != 0) return;
                Wire.beginTransmission(0x3c);
                Wire.write(uint8_t(0x40)); Wire.write(uint8_t(0x5a));
                if (Wire.endTransmission() == 0 && analogRead(1) > 0)
                    Serial.println("published:esp32-s3:проверка");
            }
            void loop() { digitalWrite(39, digitalRead(12)); delay(1); }
            """;
        const string marker = "published:esp32-s3:проверка";
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var image = await Esp32S3Compiler.CompileArduino(code, deadline.Token);
        var serial = "";
        await Esp32S3GpioRunner.Run(image, runtime, async (session, cancellation) =>
        {
            using var displayWorker = new Esp32S3I2cWorker();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            startup.CancelAfter(TimeSpan.FromSeconds(20));
            Esp32S3I2cDevice[] devices = [new("oled", 0x3c, 128, 64, true, true, "ssd1306", 8, 9)];
            var inputs = new Dictionary<int, bool?> { [12] = null, [39] = null };
            bool adcReported = false, gpioHigh = false, oledReady = false;
            try
            {
                while (!adcReported || !gpioHigh || !oledReady)
                {
                    var display = (await session.ExchangeI2c(displayWorker, devices, startup.Token)).OledDisplays.SingleOrDefault();
                    var pins = await session.ExchangePads(inputs, startup.Token);
                    serial += (await session.ExchangeSerial(null, startup.Token)).Ports[0];
                    adcReported = serial.Contains(marker, StringComparison.Ordinal);
                    gpioHigh = pins[39] is { Driven: true, OutputHigh: true };
                    oledReady = display is { Display: true } && Convert.FromBase64String(display.Memory)[0] == 0x5a;
                    if (!adcReported || !gpioHigh || !oledReady) await Task.Delay(15, startup.Token);
                }

                startup.CancelAfter(TimeSpan.FromSeconds(10));
                inputs[12] = false;
                while (true)
                {
                    var pins = await session.ExchangePads(inputs, startup.Token);
                    if (!pins[12].InputHigh && pins[39] is { Driven: true, OutputHigh: false }) break;
                    await Task.Delay(15, startup.Token);
                }
            }
            catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException($"Packaged ESP32-S3 did not complete: ADC via UART={adcReported}, GPIO high={gpioHigh}, OLED={oledReady}, button sent={inputs[12] == false}.", e);
            }
        }, deadline.Token, initialAnalog: new Dictionary<int, int> { [1] = 1_800_000 }, displayBus: true);
        return new { board = "esp32-s3", compiled = true, executed = true, gpio = true, adc = true, oled = true, serial,
            firmwareSha256 = Convert.ToHexString(SHA256.HashData(image.Flash)) };
    }
}
