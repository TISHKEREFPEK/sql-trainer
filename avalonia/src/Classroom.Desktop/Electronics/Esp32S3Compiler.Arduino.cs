using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed record Esp32S3SketchImage(byte[] Elf, byte[] Bootloader, byte[] Partitions, byte[] Application, byte[] Flash);

public static partial class Esp32S3Compiler
{
    public const string ArduinoVersion="3.3.12";
    const string ArduinoFqbn="esp32:esp32:esp32s3:FlashMode=dio,FlashSize=4M,PSRAM=disabled,CDCOnBoot=default";
    static string Platform(string runtime)=>Path.Combine(runtime,"data","packages","esp32","hardware","esp32",ArduinoVersion);
    static string Sdk(string runtime)=>Path.Combine(runtime,"data","packages","esp32","tools","esp32s3-libs",ArduinoVersion);
    public static bool ArduinoAvailable
    {
        get
        {
            var runtime = Runtime;
            return Available &&
                File.Exists(Path.Combine(runtime, "data", "inventory.yaml")) &&
                File.Exists(Path.Combine(runtime, "data", "packages", "esp32", "tools", "esptool_py", "5.3.1", "esptool.exe")) &&
                File.Exists(Path.Combine(Platform(runtime), "platform.txt")) &&
                File.Exists(Path.Combine(Sdk(runtime), "flags", "cpp_flags")) &&
                File.Exists(Path.Combine(runtime, "sandbox-xtensa", "ctags", "ctags.exe")) &&
                File.Exists(Path.Combine(runtime, "sandbox-xtensa", "gnu-runtime", "xtensa-esp-elf", "include", "c++", "14.2.0", "string"));
        }
    }
    static readonly HashSet<string> GccOnly=["-mlongcalls","-mdisable-hardware-atomics","-freorder-blocks","-fstrict-volatile-bitfields","-fno-tree-switch-conversion","-Wno-error=maybe-uninitialized","-Wno-old-style-declaration"];
    static Dictionary<string,string> ArduinoFlags(string runtime)=>new[]{"c_flags","cpp_flags","S_flags","ld_flags"}.ToDictionary(name=>name,name=>string.Join(' ',File.ReadAllText(Path.Combine(Sdk(runtime),"flags",name)).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Where(flag=>!GccOnly.Contains(flag))));
    static Dictionary<string,string> ArduinoProperties(string runtime)
    {
        var common="-no-canonical-prefixes -fintegrated-cc1 -fintegrated-as -fno-vectorize -fno-slp-vectorize --target=xtensa-esp-elf -mcpu=esp32s3 -nostdlibinc -isystem \"{classroom.gnu.path}/xtensa-esp-elf/include\" -isystem \"{classroom.gnu.path}/lib/gcc/xtensa-esp-elf/14.2.0/include-fixed\"";
        var cxx=" -nostdinc++ -isystem \"{classroom.gnu.path}/xtensa-esp-elf/include/c++/14.2.0\" -isystem \"{classroom.gnu.path}/xtensa-esp-elf/include/c++/14.2.0/xtensa-esp-elf/esp32s3/no-rtti\" -isystem \"{classroom.gnu.path}/xtensa-esp-elf/include/c++/14.2.0/backward\"";
        var linker="-no-canonical-prefixes --target=xtensa-esp-elf -mcpu=esp32s3 --ld-path={compiler.path}xtensa-esp32s3-elf-clang-ld.exe -nostdlib -L\"{classroom.gnu.path}/xtensa-esp-elf/lib/esp32s3/no-rtti\" -L\"{classroom.gnu.path}/lib/gcc/xtensa-esp-elf/14.2.0/esp32s3\"";
        var properties=new Dictionary<string,string>{
            ["compiler.path"]=Path.GetDirectoryName(Tool(runtime,"clang"))!+Path.DirectorySeparatorChar,
            ["compiler.optimization_flags"]="-O1",
            ["compiler.prefix"]="",["compiler.c.cmd"]="clang.exe",["compiler.cpp.cmd"]="clang++.exe",["compiler.S.cmd"]="clang.exe",["compiler.c.elf.cmd"]="clang++.exe",["compiler.ar.cmd"]="llvm-ar.exe",["compiler.size.cmd"]="llvm-size.exe",
            ["runtime.tools.ctags.path"]=Path.Combine(runtime,"sandbox-xtensa","ctags"),["classroom.gnu.path"]=Path.Combine(runtime,"sandbox-xtensa","gnu-runtime"),
            ["compiler.c.extra_flags"]=common,["compiler.cpp.extra_flags"]=common+cxx,["compiler.S.extra_flags"]=common,["compiler.c.elf.extra_flags"]=linker
        };
        var upstream=File.ReadAllLines(Path.Combine(Platform(runtime),"platform.txt"));
        foreach(var (key,flag) in new[]{("compiler.c.flags","c_flags"),("compiler.cpp.flags","cpp_flags"),("compiler.S.flags","S_flags"),("compiler.c.elf.flags","ld_flags")}){
            var original=upstream.Single(l=>l.StartsWith(key+"=",StringComparison.Ordinal))[(key.Length+1)..];
            var needle="@{compiler.sdk.path}/flags/"+flag;
            if(original.Split(needle,StringSplitOptions.None).Length!=2)throw new InvalidDataException("Неизвестный формат флагов Arduino ESP32-S3.");
            properties[key]=original.Replace(needle,"@{build.path}/classroom-"+flag+".rsp");
        }
        return properties;
    }
    static string ProfileHash(Dictionary<string,string> props,Dictionary<string,string> flags)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("arduino-clang-v1\n"+ArduinoFqbn+"\n"+string.Join('\n',props.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))+"\n"+string.Join('\n',flags.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value)))));
    static async Task<(string[] Args,Dictionary<string,string> Settings)> ArduinoCommand(string runtime,string scratch,Dictionary<string,string> props,Dictionary<string,string> flags,CancellationToken cancellation)
    {
        var build=Path.Combine(scratch,"build");Directory.CreateDirectory(build);foreach(var p in flags)await File.WriteAllTextAsync(Path.Combine(build,"classroom-"+p.Key+".rsp"),p.Value,cancellation);
        var config=Path.Combine(scratch,"arduino-cli.yaml");await File.WriteAllTextAsync(config,Wire.Write(new{metrics=new{enabled=false},updater=new{enable_notification=false}}),cancellation);
        var args=new List<string>{"--config-file",config,"--no-color","compile","--fqbn",ArduinoFqbn,"--build-path",build,"--warnings","all","--jobs","2"};foreach(var p in props){args.Add("--build-property");args.Add(p.Key+"="+p.Value);}args.Add(Path.Combine(scratch,"sketch"));
        var settings=new Dictionary<string,string>{{"ARDUINO_DIRECTORIES_DATA",Path.Combine(runtime,"data")},{"ARDUINO_DIRECTORIES_DOWNLOADS",Path.Combine(scratch,"downloads")},{"ARDUINO_DIRECTORIES_USER",Path.Combine(runtime,"user")},{"ARDUINO_BUILD_CACHE_PATH",Path.Combine(scratch,"cache")}};
        return(args.ToArray(),settings);
    }
    [SupportedOSPlatform("windows")]
    static InvalidDataException ArduinoError(string stage,WindowsSandboxProcess.Result result)
    {
        var log=result.Error+"\n"+result.Output;
        var errors=string.Join('\n',log.Split('\n').Where(l=>new[]{": error:","fatal error:","undefined reference","undefined symbol","LLVM ERROR","ld returned"}.Any(s=>l.Contains(s,StringComparison.OrdinalIgnoreCase))).Take(24));
        if(errors.Length>2400)errors=errors[..2400]+"\n…";
        return new InvalidDataException($"{stage} ESP32-S3 (код 0x{result.ExitCode:X8}):\n"+(log.Length<=12000?log:errors+"\n"+log[..3000]+"\n…\n"+log[^6000..]));
    }
    public static async Task<Esp32S3SketchImage> CompileArduino(string code,CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Компиляция ESP32-S3 требует Windows 10 или новее.");
        if (!ArduinoAvailable) throw new IOException("Не подготовлен Arduino SDK ESP32-S3.");
        if (code is null || code.Length > 100_000) throw new InvalidDataException("Некорректный скетч ESP32-S3.");
        await slots.WaitAsync(cancellation);
        var scratch = Scratch();
        try
        {
            var runtime = await CompilerRuntime.Prepare(Runtime, cancellation, extraDirectory: "sandbox-xtensa");
            var properties = ArduinoProperties(runtime);
            var flags = ArduinoFlags(runtime);
            var core = await PrepareArduinoCore(runtime, properties, flags, cancellation);
            var sketch = Directory.CreateDirectory(Path.Combine(scratch, "sketch"));
            await File.WriteAllTextAsync(Path.Combine(sketch.FullName, "sketch.ino"), code, cancellation);
            var command = await ArduinoCommand(runtime, scratch, properties, flags, cancellation);
            var result = await WindowsSandboxProcess.RunWithPreparedFiles(Path.Combine(runtime, "arduino-cli.exe"),
                command.Args, runtime, scratch, command.Settings, cancellation, alias => StageArduinoCore(runtime, scratch, alias, core));
            if (result.ExitCode != 0) throw ArduinoError("Ошибка компиляции Arduino", result);

            async Task<byte[]> Read(string extension, int maximum)
            {
                var file = new FileInfo(Path.Combine(scratch, "build", "sketch.ino." + extension));
                CheckArduinoPath(scratch, file.FullName);
                if (!file.Exists || file.Length < 1 || file.Length > maximum)
                    throw new InvalidDataException("Некорректный результат Arduino: " + extension);
                return await File.ReadAllBytesAsync(file.FullName, cancellation);
            }
            return new(await Read("elf", 128 * 1024 * 1024), await Read("bootloader.bin", 0x8000),
                await Read("partitions.bin", 0x1000), await Read("bin", 0x140000), await Read("merged.bin", 4 * 1024 * 1024));
        }
        finally
        {
            try { CleanScratch(scratch); }
            finally { slots.Release(); }
        }
    }
    sealed record ArduinoCore(string Directory,string Fqbn,string Suffix,string Optimization,string Sha256);
    [SupportedOSPlatform("windows")]
    static async Task<ArduinoCore> PrepareArduinoCore(string runtime,Dictionary<string,string> props,Dictionary<string,string> flags,CancellationToken cancellation)
    {
        var hash=ProfileHash(props,flags);var root=Path.Combine(runtime,"esp32s3-core-cache");CheckArduinoPath(runtime,root);Directory.CreateDirectory(root);var target=Path.Combine(root,hash);var marker=Path.Combine(target,"ready.json");
        var lockPath=Path.Combine(root,hash+".lock");
        CheckArduinoPath(runtime,lockPath);
        FileStream? lease = null;
        for (var attempt = 0; lease is null; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 && attempt < 3600)
            { await Task.Delay(100, cancellation); }
        }
        using(lease){
            if(File.Exists(marker)){CheckArduinoPath(runtime,marker);if(new FileInfo(marker).Length is <1 or >16384)throw new InvalidDataException("Повреждён индекс ядра ESP32-S3.");var ready=Wire.Read<ArduinoCore>(File.ReadAllText(marker));CheckArduinoCore(runtime,ready,target);return ready;}
            if(Directory.Exists(target))throw new IOException("Незавершённый кэш ядра ESP32-S3 сохранён для проверки.");
            var scratch=Scratch();var stage=target+".staging-"+Guid.NewGuid().ToString("N");
            try{
                Directory.CreateDirectory(Path.Combine(scratch,"sketch"));await File.WriteAllTextAsync(Path.Combine(scratch,"sketch","sketch.ino"),"void setup(){} void loop(){}\n",cancellation);
                var command=await ArduinoCommand(runtime,scratch,props,flags,cancellation);
                var properties=await WindowsSandboxProcess.PrepareTrustedRuntime(Path.Combine(runtime,"arduino-cli.exe"),command.Args.Append("--show-properties=expanded"),runtime,scratch,command.Settings,cancellation);
                if(properties.ExitCode!=0)throw ArduinoError("Ошибка подготовки параметров Arduino",properties);
                var values=properties.Output.Split('\n').Where(s=>s.Contains('=')).Select(s=>s.TrimEnd('\r').Split('=',2)).GroupBy(p=>p[0]).ToDictionary(g=>g.Key,g=>g.Last()[1]);
                var result=await WindowsSandboxProcess.PrepareTrustedRuntime(Path.Combine(runtime,"arduino-cli.exe"),command.Args,runtime,scratch,command.Settings,cancellation);
                // This preparation never receives pupil source. Keep the full
                // bounded SDK log, rather than losing a fatal line among vendor
                // warnings in the smaller user-facing exception.
                var log=Path.Combine(root,hash+".prepare-log.txt");CheckArduinoPath(runtime,log);await File.WriteAllTextAsync(log,result.Error+"\n"+result.Output,cancellation);
                if(result.ExitCode!=0)throw ArduinoError("Ошибка подготовки фиксированного ядра Arduino",result);
                var source=Path.Combine(scratch,"build","core","core.a");if(!File.Exists(source))throw new IOException("Не создан архив ядра ESP32-S3.");
                var suffix=values["runtime.platform.path"][1..];var ready=new ArduinoCore(target,values["build.fqbn"],suffix,values.GetValueOrDefault("compiler.optimization_flags",""),ArduinoHash(source));
                Directory.CreateDirectory(stage);File.Copy(source,Path.Combine(stage,"core.a"));await File.WriteAllTextAsync(Path.Combine(stage,"ready.json"),Wire.Write(ready),cancellation);WindowsSandboxProcess.GrantRuntimeRead(stage);Directory.Move(stage,target);CheckArduinoCore(runtime,ready,target);return ready;
            }finally{CleanScratch(scratch);if(Directory.Exists(stage)){CheckArduinoPath(runtime,stage);Directory.Delete(stage,true);}}
        }
    }
    static void StageArduinoCore(string runtime,string scratch,string alias,ArduinoCore ready)
    {
        CheckArduinoCore(runtime,ready,ready.Directory);var name=ArduinoCacheName(ready.Fqbn,alias[0]+ready.Suffix,ready.Optimization);var file=Path.Combine(scratch,"cache","cores",name,"core.a");CheckArduinoPath(scratch,file);Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.Copy(Path.Combine(ready.Directory,"core.a"),file);
    }
    static void CheckArduinoCore(string runtime,ArduinoCore ready,string expected)
    {
        if(ready.Directory!=expected||ready.Fqbn!=ArduinoFqbn||!string.Equals(ready.Suffix,@":\data\packages\esp32\hardware\esp32\3.3.12",StringComparison.OrdinalIgnoreCase)||ready.Optimization!="-O1"||ready.Sha256?.Length!=64)throw new InvalidDataException("Повреждён индекс ядра ESP32-S3.");
        var file=Path.Combine(expected,"core.a");CheckArduinoPath(runtime,file);if(!File.Exists(file)||new FileInfo(file).Length is <1 or >134217728||ArduinoHash(file)!=ready.Sha256)throw new InvalidDataException("Повреждён архив ядра ESP32-S3.");
    }
    static void CheckArduinoPath(string root,string path)
    {
        var parent=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);var full=Path.GetFullPath(path);if(!full.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Неверный путь кэша ESP32-S3.");
        for(var p=full;p.Length>parent.Length;p=Path.GetDirectoryName(p)!)if((File.Exists(p)||Directory.Exists(p))&&File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Ссылки в кэше ESP32-S3 запрещены.");
    }
    static string ArduinoHash(string file){using var source=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(source));}
#pragma warning disable CA5351
    static string ArduinoCacheName(string fqbn,string root,string optimization){static string Md5(string s)=>Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();var name=fqbn.Replace(':','_').Replace('=','_')+"_"+Md5(root+optimization);return name.Length>100?Md5(name):name;}
#pragma warning restore CA5351
}
