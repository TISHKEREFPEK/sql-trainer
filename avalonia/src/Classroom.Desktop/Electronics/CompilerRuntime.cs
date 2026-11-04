using System.Security.Cryptography;
using System.Text;

namespace Classroom.Desktop.Electronics;

public static class CompilerRuntime
{
    static readonly SemaphoreSlim gate = new(1, 1);
    static IEnumerable<string> Files(string source)
    {
        static string Regular(string file) => File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) ? throw new IOException("Ссылки в автономном комплекте компилятора не поддерживаются.") : file;
        if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Папка комплекта компилятора не должна быть ссылкой.");
        yield return Regular(Path.Combine(source, "arduino-cli.exe"));
        foreach (var name in new[] { "package_index.json", "package_index.json.sig", "library_index.json", "library_index.json.sig", "inventory.yaml" })
        {
            var file = Path.Combine(source, "data", name); if (File.Exists(file)) yield return Regular(file);
        }
        foreach (var directory in new[] { Path.Combine(source, "data", "packages"), Path.Combine(source, "user", "libraries"), Path.Combine(source, "sandbox-avr") })
            foreach (var file in Enumerate(directory)) yield return file;
    }
    static IEnumerable<string> Enumerate(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылки в автономном комплекте компилятора не поддерживаются.");
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылки в автономном комплекте компилятора не поддерживаются.");
            yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(directory)) foreach (var file in Enumerate(child)) yield return file;
    }
    public static async Task<string> Prepare(string source, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Изолированная компиляция требует Windows 10 или новее.");
        source = Path.GetFullPath(source);
        var cache = Path.GetFullPath(Environment.GetEnvironmentVariable("CLASSROOM_COMPILER_CACHE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQL-Classroom", "compiler-runtime"));
        await gate.WaitAsync(cancellation);
        try
        {
            Directory.CreateDirectory(cache);
            if (File.GetAttributes(cache).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Папка комплекта компилятора не должна быть ссылкой.");
            await using var lease = await Lock(Path.Combine(cache, "prepare.lock"), cancellation);
            var files = Files(source).Select(path => new FileInfo(path)).OrderBy(f => f.FullName, StringComparer.Ordinal).ToArray();
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v1\n" + source + "\n" + string.Join('\n', files.Select(f => Path.GetRelativePath(source, f.FullName) + ":" + f.Length + ":" + f.LastWriteTimeUtc.Ticks)))));
            var target = Path.Combine(cache, "runtime-" + fingerprint);
            if (Directory.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Папка комплекта компилятора не должна быть ссылкой.");
            if (File.Exists(Path.Combine(target, "ready.txt"))) return target;
            var staging = target + ".building-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(staging);
            try
            {
                await Task.Run(() =>
                {
                    if (!OperatingSystem.IsWindows()) throw new IOException("Изолированная компиляция требует Windows.");
                    foreach (var file in files)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(source, file.FullName);
                        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) throw new IOException("Комплект выходит за исходную папку.");
                        var destination = Path.Combine(staging, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file.FullName, destination);
                    }
                    Directory.CreateDirectory(Path.Combine(staging, "downloads"));
                    WindowsSandboxProcess.GrantRuntimeRead(staging);
                    File.WriteAllText(Path.Combine(staging, "ready.txt"), fingerprint);
                }, cancellation);
                CheckChild(staging, cache); CheckChild(target, cache);
                Directory.Move(staging, target);
                return target;
            }
            finally { if (Directory.Exists(staging)) { CheckChild(staging, cache); Directory.Delete(staging, true); } }
        }
        finally { gate.Release(); }
    }
    static void CheckChild(string target, string root)
    {
        if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            File.GetAttributes(Path.GetDirectoryName(target)!).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Некорректная папка комплекта компилятора.");
    }
    static async Task<FileStream> Lock(string path, CancellationToken cancellation)
    {
        for (var i = 0; ; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 && i < 600) { await Task.Delay(100, cancellation); }
        }
    }
}
