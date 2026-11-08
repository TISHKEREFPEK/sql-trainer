using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public enum PicoSourceKind{Arduino,BareMetal}

// Platform foundation only: the remaining ROM math/peripheral ABI and native
// circuit integration are required before Pico becomes a runnable editor part.
public static partial class PicoCompiler
{
    public const string CoreVersion="6.3.2",ToolsVersion="5.0.0-9576866";
    public const string IndexUrl="https://github.com/earlephilhower/arduino-pico/releases/download/global/package_rp2040_index.json";
    static readonly SemaphoreSlim slots=new(1,1);
    public static string Runtime
    {
        get
        {
            if(Environment.GetEnvironmentVariable("CLASSROOM_PICO_RUNTIME") is { }path)return Path.GetFullPath(path);
            var deployed=Path.Combine(AppContext.BaseDirectory,"electronics","pico-runtime");if(Directory.Exists(deployed))return deployed;
            var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Classroom.sln")))root=root.Parent;
            return root is null?deployed:Path.Combine(root.FullName,"artifacts","pico-runtime");
        }
    }
    static string Tool(string root,string name)=>Path.Combine(root,"sandbox-arm","bin","arm-none-eabi-"+name+".exe");
    public static bool Available=>File.Exists(Path.Combine(Runtime,"arduino-cli.exe"))&&File.Exists(Tool(Runtime,"g++"))&&File.Exists(Path.Combine(Runtime,"sandbox-arm","ctags.exe"))&&File.Exists(Path.Combine(Runtime,"data","packages","rp2040","hardware","rp2040",CoreVersion,"platform.txt"));
    public static async Task<byte[]> Compile(string code,CancellationToken cancellation,PicoSourceKind kind=PicoSourceKind.Arduino)
    {
        if(!OperatingSystem.IsWindows())throw new IOException("Изолированная компиляция Pico требует Windows 10 или новее.");
        if(!Available)throw new IOException("Не подготовлен отдельный автономный комплект Raspberry Pi Pico.");
        if(!Enum.IsDefined(kind)||code is null||code.Length>100000)throw new InvalidDataException("Некорректная программа Pico.");
        var scratch=Path.Combine(Path.GetTempPath(),"classroom-pico-"+Guid.NewGuid().ToString("N"));await slots.WaitAsync(cancellation);
        try
        {
            var runtime=await CompilerRuntime.Prepare(Runtime,cancellation,extraDirectory:"sandbox-arm");
            if(kind==PicoSourceKind.Arduino)await PrepareCore(runtime,cancellation);
            Directory.CreateDirectory(scratch);
            var settings=new Dictionary<string,string>{{"ARDUINO_DIRECTORIES_DATA",Path.Combine(runtime,"data")},{"ARDUINO_DIRECTORIES_DOWNLOADS",Path.Combine(scratch,"downloads")},{"ARDUINO_DIRECTORIES_USER",Path.Combine(scratch,"user")},{"ARDUINO_BUILD_CACHE_PATH",Path.Combine(scratch,"cache")}};
            async Task Run(string executable,IEnumerable<string> arguments)
            {
                var result=await Task.Run(()=>OperatingSystem.IsWindows()?(kind==PicoSourceKind.Arduino?WindowsSandboxProcess.RunWithPreparedFiles(executable,arguments,runtime,scratch,settings,cancellation,alias=>StageCore(runtime,scratch,alias)):WindowsSandboxProcess.Run(executable,arguments,runtime,scratch,settings,cancellation)):throw new IOException("Компиляция Pico требует Windows."),cancellation);
                if(result.ExitCode!=0){var log=result.Output+"\n"+result.Error;throw new InvalidDataException("Ошибка компиляции Pico:\n"+log[..Math.Min(8000,log.Length)]);}
            }
            if(kind==PicoSourceKind.Arduino)
            {
                var sketch=Path.Combine(scratch,"sketch");Directory.CreateDirectory(sketch);await File.WriteAllTextAsync(Path.Combine(sketch,"sketch.ino"),code,cancellation);
                var config=Path.Combine(scratch,"arduino-cli.yaml");await File.WriteAllTextAsync(config,Wire.Write(new{board_manager=new{additional_urls=new[]{IndexUrl}},metrics=new{enabled=false},updater=new{enable_notification=false}}),cancellation);
                var output=Path.Combine(scratch,"output");
                await Run(Path.Combine(runtime,"arduino-cli.exe"),new[]{"--config-file",config,"--no-color","compile","--fqbn","rp2040:rp2040:rpipico:freq=125,flash=2097152_0","--output-dir",output,"--build-property","compiler.path="+Path.Combine(runtime,"sandbox-arm","bin")+Path.DirectorySeparatorChar,"--build-property","runtime.tools.ctags.path="+Path.Combine(runtime,"sandbox-arm"),"--warnings","all","--jobs","2",sketch});
                var image=await File.ReadAllBytesAsync(Path.Combine(output,"sketch.ino.uf2"),cancellation);ValidateSize(image);return image;
            }
            var source=Path.Combine(scratch,"firmware.cpp");await File.WriteAllTextAsync(source,code,cancellation);
            var linker=Path.Combine(scratch,"firmware.ld");await File.WriteAllTextAsync(linker,"ENTRY(reset)\nMEMORY { FLASH (rx) : ORIGIN = 0x10000000, LENGTH = 2M RAM (rwx) : ORIGIN = 0x20000000, LENGTH = 264K }\nSECTIONS { .text : { KEEP(*(.vectors)) *(.text*) *(.rodata*) } > FLASH .data : { *(.data*) } > RAM AT> FLASH .bss (NOLOAD) : { *(.bss*) *(COMMON) } > RAM /DISCARD/ : { *(.ARM.exidx*) *(.ARM.extab*) *(.comment*) *(.eh_frame*) } }\n",cancellation);
            var elf=Path.Combine(scratch,"firmware.elf");var binary=Path.Combine(scratch,"firmware.bin");
            await Run(Tool(runtime,"g++"),new[]{"-mcpu=cortex-m0plus","-mthumb","-mfloat-abi=soft","-std=c++17","-Os","-ffreestanding","-fno-builtin","-fno-delete-null-pointer-checks","-fno-exceptions","-fno-rtti","-nostdlib","-Wl,--gc-sections","-Wl,-T,"+linker,source,"-lgcc","-o",elf});
            await Run(Tool(runtime,"objcopy"),new[]{"-Obinary",elf,binary});return ToUf2(await File.ReadAllBytesAsync(binary,cancellation));
        }
        finally
        {
            var path=Path.GetFullPath(scratch);var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            try{if(!path.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(path).StartsWith("classroom-pico-",StringComparison.Ordinal))throw new IOException("Некорректная папка компиляции Pico.");if(Directory.Exists(path))Directory.Delete(path,true);}
            finally{slots.Release();}
        }
    }
    static void ValidateSize(byte[] image)
    {if(image.Length<512||image.Length>4*1024*1024||image.Length%512!=0)throw new InvalidDataException("Некорректный размер UF2 Pico.");}
    internal static byte[] ToUf2(byte[] binary)
    {
        if(binary.Length<8||binary.Length>2*1024*1024)throw new InvalidDataException("Программа выходит за 2 МиБ flash Pico.");
        var blocks=(binary.Length+255)/256;var image=new byte[blocks*512];
        using var writer=new BinaryWriter(new MemoryStream(image));
        for(var i=0;i<blocks;i++)
        {
            writer.BaseStream.Position=i*512;writer.Write(0x0a324655u);writer.Write(0x9e5d5157u);writer.Write(0x2000u);writer.Write(0x10000000u+(uint)i*256);writer.Write(256u);writer.Write((uint)i);writer.Write((uint)blocks);writer.Write(0xe48bff56u);
            writer.Write(binary,i*256,Math.Min(256,binary.Length-i*256));writer.BaseStream.Position=(i+1)*512-4;writer.Write(0x0ab16f30u);
        }
        return image;
    }
}
