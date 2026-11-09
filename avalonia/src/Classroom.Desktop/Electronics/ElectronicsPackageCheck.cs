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
            Environment.SetEnvironmentVariable("CLASSROOM_COMPILER_CACHE",cache);Environment.SetEnvironmentVariable("CLASSROOM_COMPILER_LEASES",Path.Combine(output,"leases"));
            var root=AppContext.BaseDirectory;
            foreach(var folder in new[]{"runtime","pico-runtime"})foreach(var file in new[]{"arduino-cli.exe","node.exe","ArduinoCLI-LICENSE.txt","Node-LICENSE.txt"})
                if(!File.Exists(Path.Combine(root,"electronics",folder,file)))throw new IOException("Published SDK file is missing: "+folder+"/"+file);
            if(Path.GetFullPath(FirmwareRunner.Runtime)!=Path.GetFullPath(Path.Combine(root,"electronics","runtime"))||Path.GetFullPath(PicoCompiler.Runtime)!=Path.GetFullPath(Path.Combine(root,"electronics","pico-runtime")))throw new IOException("Release check must use the SDKs next to this executable.");
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
}
