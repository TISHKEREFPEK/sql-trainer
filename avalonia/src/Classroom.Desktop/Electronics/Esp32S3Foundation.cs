using System.Text;

namespace Classroom.Desktop.Electronics;

public sealed record Esp32S3Run(int ExitCode,string Uart0,string Uart1,string Diagnostics)
{
    public bool StoppedAtLimit { get; init; }
}

// Bounded runs inside AppContainer. Only the bare-metal entry uses a
// semihosting exit trap. Arduino boots from flash through the board ROM.
public static class Esp32S3Foundation
{
    static readonly SemaphoreSlim slots=new(1,1);
    public static Task<Esp32S3Run> Run(byte[] elf,CancellationToken cancellation)
    {
        var imageBytes=elf?.ToArray()??throw new InvalidDataException("Отсутствует ELF-прошивка ESP32-S3.");
        Esp32S3Image.Validate(imageBytes);
        return RunImage(imageBytes, null, cancellation);
    }

    public static Task<Esp32S3Run> RunArduino(Esp32S3SketchImage sketch, TimeSpan duration, CancellationToken cancellation)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromSeconds(20))
            throw new ArgumentOutOfRangeException(nameof(duration));
        return RunImage(CopyArduinoFlash(sketch), duration, cancellation);
    }

    internal static byte[] CopyArduinoFlash(Esp32S3SketchImage sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        var flash = sketch.Flash?.ToArray() ?? throw new InvalidDataException("Отсутствует образ флеш-памяти ESP32-S3.");
        if (flash.Length != 4 * 1024 * 1024 ||
            !MatchesFirmware(flash, 0, 0x8000, sketch.Bootloader) ||
            !MatchesFirmware(flash, 0x10000, 0x140000, sketch.Application) ||
            sketch.Partitions is not { Length: > 1 and <= 0x1000 } ||
            sketch.Partitions[0] != 0xaa || sketch.Partitions[1] != 0x50 ||
            !flash.AsSpan(0x8000, sketch.Partitions.Length).SequenceEqual(sketch.Partitions))
            throw new InvalidDataException("Некорректный образ флеш-памяти Arduino ESP32-S3.");
        return flash;
    }

    static bool MatchesFirmware(byte[] flash, int offset, int maxSize, byte[]? image) =>
        image is { Length: >= 24 } && image.Length <= maxSize &&
        image[0] == 0xe9 && image[12] == 9 && image[13] == 0 &&
        flash.AsSpan(offset, image.Length).SequenceEqual(image);

    static async Task<Esp32S3Run> RunImage(byte[] imageBytes, TimeSpan? duration, CancellationToken cancellation)
    {
        if(!OperatingSystem.IsWindows())throw new IOException("Изоляция ESP32-S3 требует Windows 10 или новее.");
        await slots.WaitAsync(cancellation);var scratch=Esp32S3Compiler.Scratch();
        try
        {
            var runtime=await CompilerRuntime.Prepare(Esp32S3Compiler.Runtime,cancellation,extraDirectory:"sandbox-xtensa");
            Directory.CreateDirectory(scratch);
            var image=Path.Combine(scratch,duration is null ? "firmware.elf" : "flash.bin");
            await File.WriteAllBytesAsync(image,imageBytes,cancellation);
            string[] common = ["-machine", "esp32s3", "-accel", "tcg,tb-size=32,thread=single",
                "-display", "none", "-monitor", "none", "-serial", "file:uart0.log", "-serial", "file:uart1.log",
                "-nic", "none", "-no-reboot"];
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var files=new[]{Path.Combine(scratch,"uart0.log"),Path.Combine(scratch,"uart1.log")};
            var emulator = Esp32S3Compiler.Emulator(runtime);
            var run = duration is { } window
                ? WindowsSandboxProcess.RunFor(emulator, common.Concat(["-L", Path.Combine(runtime, "sandbox-xtensa", "qemu", "share", "qemu"),
                    "-drive", "file=flash.bin,if=mtd,format=raw"]), runtime, scratch, window, limit.Token)
                : WindowsSandboxProcess.Run(emulator, common.Concat(["-semihosting", "-kernel", image]), runtime, scratch,
                    new Dictionary<string, string>(), limit.Token);
            var oversized=false;
            try{
                while(!run.IsCompleted){
                    if(files.Any(f=>File.Exists(f)&&new FileInfo(f).Length>1024*1024)){oversized=true;limit.Cancel();break;}
                    await Task.WhenAny(run,Task.Delay(25,CancellationToken.None));
                }
                WindowsSandboxProcess.Result result;
                try{result=await run;}catch(IOException) when(oversized){throw new InvalidDataException("ESP32-S3 превысил размер UART.");}
                async Task<string> Read(string file){if(!File.Exists(file))return "";if(new FileInfo(file).Length>1024*1024)throw new InvalidDataException("ESP32-S3 превысил размер UART.");return await File.ReadAllTextAsync(file,Encoding.UTF8,cancellation);}
                return new(result.ExitCode,await Read(files[0]),await Read(files[1]),result.Error+result.Output)
                    { StoppedAtLimit = result.StoppedAtLimit };
            }finally{
                // Retire the owned process even if monitoring/reading its UART
                // files fails, before the outer finally removes scratch.
                if(!run.IsCompleted){limit.Cancel();try{await run;}catch{}}
            }
        }
        finally{try{Esp32S3Compiler.CleanScratch(scratch);}finally{slots.Release();}}
    }
}
