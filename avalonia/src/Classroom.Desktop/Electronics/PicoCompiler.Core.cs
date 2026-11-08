using Classroom.Contracts;
using System.Security.Cryptography;
using System.Runtime.Versioning;

namespace Classroom.Desktop.Electronics;

public static partial class PicoCompiler
{
    sealed record CoreCache(int Version,string Fqbn,string Optimization,string CoreSuffix,string Sha256);
    // Only this fixed, application-owned source receives the preparation budget.
    // Pupil code is not written/passed to the preparation process at all.
    [SupportedOSPlatform("windows")]
    static async Task PrepareCore(string runtime,CancellationToken cancellation)
    {
        var target=Path.Combine(runtime,"pico-core-cache");var marker=Path.Combine(target,"ready.json");
        FileStream? lease=null;
        for(var attempt=0;lease is null;attempt++)
        {cancellation.ThrowIfCancellationRequested();try{lease=new FileStream(Path.Combine(runtime,"pico-core.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException e) when((e.HResult&0xffff) is 32 or 33&&attempt<3600){await Task.Delay(100,cancellation);}}
        using(lease)
        {
            if(File.Exists(marker))
            {
                var ready=ReadCoreCache(runtime);
                if(ready.Version<3)
                {
                    var old=Path.Combine(ready.Version==1?target:Path.Combine(target,"cores"),CoreName(ready.Fqbn,"D"+ready.CoreSuffix,ready.Optimization),"core.a");
                    CheckCoreArchive(runtime,old,ready.Sha256);
                    var current=Path.Combine(target,"core.a");CheckCorePath(runtime,current);File.Copy(old,current,true);
                    CheckCoreArchive(runtime,current,ready.Sha256);
                    ElectronicsHistoryStore.Mirror(marker,Wire.Write(ready with{Version=3}));
                    WindowsSandboxProcess.GrantRuntimeRead(target);
                }
                else CheckCoreArchive(runtime,Path.Combine(target,"core.a"),ready.Sha256);
                return;
            }
            if(Directory.Exists(target))throw new IOException("Найдено незавершённое подготовленное ядро Pico. Исходники сохранены; требуется пересоздать этот кэш.");
            var scratch=Path.Combine(Path.GetTempPath(),"classroom-pico-core-"+Guid.NewGuid().ToString("N"));var stage=target+".staging-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(scratch);
            try
            {
                var sketch=Path.Combine(scratch,"sketch");Directory.CreateDirectory(sketch);await File.WriteAllTextAsync(Path.Combine(sketch,"sketch.ino"),"void setup(){} void loop(){}\n",cancellation);
                var config=Path.Combine(scratch,"arduino-cli.yaml");await File.WriteAllTextAsync(config,Wire.Write(new{board_manager=new{additional_urls=new[]{IndexUrl}},metrics=new{enabled=false},updater=new{enable_notification=false}}),cancellation);
                var settings=new Dictionary<string,string>{{"ARDUINO_DIRECTORIES_DATA",Path.Combine(runtime,"data")},{"ARDUINO_DIRECTORIES_DOWNLOADS",Path.Combine(scratch,"downloads")},{"ARDUINO_DIRECTORIES_USER",Path.Combine(scratch,"user")},{"ARDUINO_BUILD_CACHE_PATH",Path.Combine(scratch,"cache")}};
                var args=new[]{"--config-file",config,"--no-color","compile","--fqbn","rp2040:rp2040:rpipico:freq=125,flash=2097152_0","--build-path",Path.Combine(scratch,"build"),"--build-property","compiler.path="+Path.Combine(runtime,"sandbox-arm","bin")+Path.DirectorySeparatorChar,"--build-property","runtime.tools.ctags.path="+Path.Combine(runtime,"sandbox-arm"),"--jobs","2",sketch};
                [SupportedOSPlatform("windows")]
                async Task<WindowsSandboxProcess.Result> Run(IEnumerable<string> command)
                {
                    var result=await Task.Run(()=>OperatingSystem.IsWindows()?WindowsSandboxProcess.PrepareTrustedRuntime(Path.Combine(runtime,"arduino-cli.exe"),command,runtime,scratch,settings,cancellation):throw new IOException("Подготовка Pico требует Windows."),cancellation);
                    if(result.ExitCode!=0)throw new InvalidDataException("Не удалось подготовить фиксированное ядро Pico:\n"+(result.Output+"\n"+result.Error)[..Math.Min(8000,result.Output.Length+1+result.Error.Length)]);return result;
                }
                var properties=(await Run(args.Append("--show-properties=expanded"))).Output.Split('\n').Where(s=>s.Contains('=')).Select(s=>s.TrimEnd('\r').Split('=',2)).GroupBy(p=>p[0]).ToDictionary(g=>g.Key,g=>g.Last()[1]);
                await Run(args);
                var core=Path.Combine(scratch,"build","core","core.a");if(!File.Exists(core))throw new IOException("Компилятор не создал архив ядра Pico.");
                var root=properties["runtime.platform.path"];if(root.Length<3||root[1]!=':'||root[2]!='\\')throw new InvalidDataException("Неожиданный путь подготовленного ядра Pico.");
                var cache=new CoreCache(3,properties["build.fqbn"],properties.GetValueOrDefault("compiler.optimization_flags",""),root[1..],Hash(core));
                Directory.CreateDirectory(stage);
                File.Copy(core,Path.Combine(stage,"core.a"));
                await File.WriteAllTextAsync(Path.Combine(stage,"ready.json"),Wire.Write(cache),cancellation);
                WindowsSandboxProcess.GrantRuntimeRead(stage);Directory.Move(stage,target);
            }
            finally
            {
                var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                if(!Path.GetFullPath(scratch).StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(scratch).StartsWith("classroom-pico-core-",StringComparison.Ordinal))throw new IOException("Неверная папка подготовки Pico.");
                if(Directory.Exists(scratch))Directory.Delete(scratch,true);
                if(Directory.Exists(stage))
                {if(!Path.GetFullPath(stage).StartsWith(Path.GetFullPath(runtime).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Кэш Pico выходит за runtime.");Directory.Delete(stage,true);}
            }
        }
    }
    // CLI links core.a relative to the sketch build directory, so a read-only
    // runtime drive cannot be used directly. Copy the trusted archive into this
    // job's scratch volume after the actual runtime drive alias is known.
    static void StageCore(string runtime,string scratch,string runtimeAlias)
    {
        if(runtimeAlias.Length!=3||runtimeAlias[0] is <'D' or >'Z'||runtimeAlias[1]!=':'||runtimeAlias[2]!='\\')throw new IOException("Неверный диск компилятора Pico.");
        var ready=ReadCoreCache(runtime);if(ready.Version!=3)throw new InvalidDataException("Не подготовлена текущая версия ядра Pico.");
        var source=Path.Combine(runtime,"pico-core-cache","core.a");CheckCoreArchive(runtime,source,ready.Sha256);
        var target=Path.Combine(scratch,"cache","cores",CoreName(ready.Fqbn,runtimeAlias[0]+ready.CoreSuffix,ready.Optimization),"core.a");
        CheckCorePath(scratch,target);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(source,target);
    }
    static CoreCache ReadCoreCache(string runtime)
    {
        var marker=Path.Combine(runtime,"pico-core-cache","ready.json");CheckCorePath(runtime,marker);
        if(new FileInfo(marker).Length>16384)throw new InvalidDataException("Некорректный индекс ядра Pico.");
        var ready=Wire.Read<CoreCache>(File.ReadAllText(marker));
        if(ready.Version is not(1 or 2 or 3)||ready.Fqbn!="rp2040:rp2040:rpipico:freq=125,flash=2097152_0"||ready.Optimization!=""||!string.Equals(ready.CoreSuffix,@":\data\packages\rp2040\hardware\rp2040\6.3.2",StringComparison.OrdinalIgnoreCase)||ready.Sha256 is null||ready.Sha256.Length!=64)throw new InvalidDataException("Неизвестная версия подготовленного ядра Pico.");
        return ready;
    }
    static void CheckCorePath(string root,string path)
    {
        var parent=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);var full=Path.GetFullPath(path);
        if(!full.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Путь ядра Pico выходит за разрешённую папку.");
        for(var item=full;item.Length>parent.Length;item=Path.GetDirectoryName(item)!)
            if((File.Exists(item)||Directory.Exists(item))&&File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Ссылки в кэше ядра Pico запрещены.");
    }
    static void CheckCoreArchive(string runtime,string file,string expected)
    {
        CheckCorePath(runtime,file);
        if(!File.Exists(file)||new FileInfo(file).Length is <1 or >67108864||Hash(file)!=expected)throw new InvalidDataException("Повреждено подготовленное ядро Pico. Пересоздайте комплект компилятора.");
    }
    static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    // Arduino CLI 1.5.1 core.go uses MD5 only to name its cache directories.
    // SHA256 above verifies the archive; MD5 is not a security decision here.
#pragma warning disable CA5351
    static string CoreName(string fqbn,string root,string optimization)
    {
        static string Md5(string value)=>Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var name=fqbn.Replace(':','_').Replace('=','_')+"_"+Md5(root+optimization);return name.Length>100?Md5(name):name;
    }
#pragma warning restore CA5351
}
