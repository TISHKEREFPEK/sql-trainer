using System.Text.Json;

namespace Classroom.Desktop.Electronics;

public static partial class Esp32S3Compiler
{
    public const string ClangVersion="19.1.2_20250312",QemuVersion="9.2.2_20260417";
    static readonly SemaphoreSlim slots=new(1,1);
    internal static string BundledRuntime => Path.Combine(AppContext.BaseDirectory, "electronics", "esp32s3-runtime");
    public static string Runtime
    {
        get
        {
            if(Environment.GetEnvironmentVariable("CLASSROOM_ESP32S3_RUNTIME") is {}path)return Path.GetFullPath(path);
            var deployed=BundledRuntime;if(Directory.Exists(deployed))return deployed;
            var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Classroom.sln")))root=root.Parent;
            return root is null?deployed:Path.Combine(root.FullName,"artifacts","esp32s3-runtime");
        }
    }
    internal static string Tool(string root,string tool)=>Path.Combine(root,"sandbox-xtensa","esp-clang","bin",tool+".exe");
    internal static string Emulator(string root)=>Path.Combine(root,"sandbox-xtensa","qemu","bin","qemu-system-xtensa.exe");
    public static bool Available
    {
        get
        {
            if(!File.Exists(Tool(Runtime,"clang++"))||!File.Exists(Tool(Runtime,"xtensa-esp32s3-elf-clang-ld"))||!File.Exists(Emulator(Runtime)))return false;
            try{using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(Runtime,"offline-esp32s3-foundation-v1.json")));var root=doc.RootElement;return root.GetProperty("version").GetInt32()==1&&root.GetProperty("clang").GetString()==ClangVersion&&root.GetProperty("qemu").GetString()==QemuVersion&&root.GetProperty("linker").GetString()=="GNU 2.43.1";}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException){return false;}
        }
    }
    const string Startup="""
        .section .text.startup,"ax",@progbits
        .align 4
        .global _start
        _start:
          movi a1,0x3fcc0000
          call0 classroom_main
          movi a2,1
          movi a3,0
          simcall
          j .
        .section .note.GNU-stack,"",@progbits
        """;
    const string Linker="""
        ENTRY(_start)
        MEMORY { CODE (rx) : ORIGIN = 0x40378000, LENGTH = 128K DATA (rw) : ORIGIN = 0x3fc90000, LENGTH = 128K }
        SECTIONS {
          .text : { *(.literal*) KEEP(*(.text.startup)) *(.text*) *(.rodata*) } > CODE
          .data : { *(.data*) } > DATA
          .bss (NOLOAD) : { *(.bss*) *(COMMON) } > DATA
          /DISCARD/ : { *(.comment*) *(.eh_frame*) *(.xtensa.info*) }
        }
        """;
    public static async Task<byte[]> CompileBareMetal(string code,CancellationToken cancellation)
    {
        if(!OperatingSystem.IsWindows())throw new IOException("Изолированная компиляция ESP32-S3 требует Windows 10 или новее.");
        if(!Available)throw new IOException("Не подготовлен автономный комплект основания ESP32-S3.");
        if(code is null||code.Length>100000)throw new InvalidDataException("Некорректная программа ESP32-S3.");
        await slots.WaitAsync(cancellation);var scratch=Scratch();
        try
        {
            var runtime=await CompilerRuntime.Prepare(Runtime,cancellation,extraDirectory:"sandbox-xtensa");Directory.CreateDirectory(scratch);
            var source=Path.Combine(scratch,"firmware.cpp");var startup=Path.Combine(scratch,"startup.S");var linker=Path.Combine(scratch,"firmware.ld");var elf=Path.Combine(scratch,"firmware.elf");
            await File.WriteAllTextAsync(source,"#include <stdint.h>\n"+code,cancellation);await File.WriteAllTextAsync(startup,Startup,cancellation);await File.WriteAllTextAsync(linker,Linker,cancellation);
            var args=new[]{"-no-canonical-prefixes","-fintegrated-cc1","--ld-path="+Tool(runtime,"xtensa-esp32s3-elf-clang-ld"),"--target=xtensa-esp-elf","-mcpu=esp32s3","-Xclang","-target-feature","-Xclang","-windowed","-fintegrated-as","-std=c++17","-Os","-ffreestanding","-fno-exceptions","-fno-rtti","-nostdlib","-Wl,--gc-sections","-T",linker,source,startup,"-o",elf};
            var result=await WindowsSandboxProcess.Run(Tool(runtime,"clang++"),args,runtime,scratch,new Dictionary<string,string>(),cancellation);
            if(result.ExitCode!=0){var text=result.Output+"\n"+result.Error;throw new InvalidDataException($"Ошибка компиляции ESP32-S3 (код 0x{result.ExitCode:X8}):\n"+text[..Math.Min(8000,text.Length)]);}
            var image=await File.ReadAllBytesAsync(elf,cancellation);Esp32S3Image.Validate(image);return image;
        }
        finally{try{CleanScratch(scratch);}finally{slots.Release();}}
    }
    internal static string Scratch()=>Path.Combine(Path.GetTempPath(),"classroom-esp32s3-"+Guid.NewGuid().ToString("N"));
    internal static void CleanScratch(string path)
    {
        var full=Path.GetFullPath(path);var parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(parent,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(full).StartsWith("classroom-esp32s3-",StringComparison.Ordinal))throw new IOException("Некорректная временная папка ESP32-S3.");
        if(Directory.Exists(full))Directory.Delete(full,true);
    }
}
