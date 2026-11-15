using System.Text;

namespace Classroom.Desktop.Electronics;

public sealed record Esp32S3Run(int ExitCode,string Uart0,string Uart1,string Diagnostics);

// Bounded bare-metal probe. Returning from classroom_main exits via Xtensa's
// semihosting trap, inside AppContainer. Network and host monitor are disabled.
// This is not an interactive runner or a substitute for missing peripherals.
public static class Esp32S3Foundation
{
    static readonly SemaphoreSlim slots=new(1,1);
    public static async Task<Esp32S3Run> Run(byte[] elf,CancellationToken cancellation)
    {
        if(!OperatingSystem.IsWindows())throw new IOException("Изоляция ESP32-S3 требует Windows 10 или новее.");
        var imageBytes=elf?.ToArray()??throw new InvalidDataException("Отсутствует ELF-прошивка ESP32-S3.");
        Esp32S3Image.Validate(imageBytes);await slots.WaitAsync(cancellation);var scratch=Esp32S3Compiler.Scratch();
        try
        {
            var runtime=await CompilerRuntime.Prepare(Esp32S3Compiler.Runtime,cancellation,extraDirectory:"sandbox-xtensa");Directory.CreateDirectory(scratch);var image=Path.Combine(scratch,"firmware.elf");await File.WriteAllBytesAsync(image,imageBytes,cancellation);
            var args=new[]{"-machine","esp32s3","-accel","tcg,tb-size=32,thread=single","-display","none","-monitor","none","-serial","file:uart0.log","-serial","file:uart1.log","-nic","none","-no-reboot","-semihosting","-kernel",image};
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var files=new[]{Path.Combine(scratch,"uart0.log"),Path.Combine(scratch,"uart1.log")};
            var run=WindowsSandboxProcess.Run(Esp32S3Compiler.Emulator(runtime),args,runtime,scratch,new Dictionary<string,string>(),limit.Token);var oversized=false;
            try{
                while(!run.IsCompleted){
                    if(files.Any(f=>File.Exists(f)&&new FileInfo(f).Length>1024*1024)){oversized=true;limit.Cancel();break;}
                    await Task.WhenAny(run,Task.Delay(25,CancellationToken.None));
                }
                WindowsSandboxProcess.Result result;
                try{result=await run;}catch(IOException) when(oversized){throw new InvalidDataException("ESP32-S3 превысил размер UART.");}
                async Task<string> Read(string file){if(!File.Exists(file))return "";if(new FileInfo(file).Length>1024*1024)throw new InvalidDataException("ESP32-S3 превысил размер UART.");return await File.ReadAllTextAsync(file,Encoding.UTF8,cancellation);}
                return new(result.ExitCode,await Read(files[0]),await Read(files[1]),result.Error+result.Output);
            }finally{
                // Retire the owned process even if monitoring/reading its UART
                // files fails, before the outer finally removes scratch.
                if(!run.IsCompleted){limit.Cancel();try{await run;}catch{}}
            }
        }
        finally{try{Esp32S3Compiler.CleanScratch(scratch);}finally{slots.Release();}}
    }
}
