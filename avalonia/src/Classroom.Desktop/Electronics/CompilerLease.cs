using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Classroom.Desktop.Electronics;

[SupportedOSPlatform("windows")]
public sealed class CompilerLease : IDisposable
{
    public sealed record Drive(string Device, string Target);
    public sealed record Record(string Id, int Pid, long Started, string Runtime, string Scratch, bool ProfileCreated, List<Drive> Drives, bool Closed = false);
    const string Prefix = "SQLClassroom.Compile.";
    readonly string file;
    Record record;
    bool disposed;
    public string ProfileName => Prefix + record.Id;
    static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("CLASSROOM_COMPILER_LEASES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQL-Classroom", "compiler-leases"));
    public CompilerLease(string runtime, string scratch, string? journalRoot = null)
    {
        var root = Path.GetFullPath(journalRoot ?? Root);
        RecoverOrphans(root);
        using var self = Process.GetCurrentProcess();
        record = new(Guid.NewGuid().ToString("N"), self.Id, self.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(runtime), Path.GetFullPath(scratch), false, []);
        Directory.CreateDirectory(root); file = Path.Combine(root, record.Id + ".json"); Save();
    }
    void Save()
    {
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(output, record); output.Flush(true); }
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, file, true); break; }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && (e.HResult & 0xffff) is 5 or 32 or 33 && attempt < 8)
                { Thread.Sleep(25 * (attempt + 1)); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void MarkProfile() { record = record with { ProfileCreated = true }; Save(); }
    public void AddDrive(string device, string target) { record.Drives.Add(new(device, target)); Save(); }
    static bool Alive(Record value)
    {
        try { using var owner = Process.GetProcessById(value.Pid); return !owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == value.Started; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
    static bool Valid(Record value, string file)
    {
        if (!Guid.TryParseExact(value.Id, "N", out _) || Path.GetFileName(file) != value.Id + ".json" || value.Runtime is null || value.Scratch is null || value.Drives is null || value.Drives.Count > 2) return false;
        var targets = new[] { "\\??\\" + Path.GetFullPath(value.Runtime), "\\??\\" + Path.GetFullPath(value.Scratch) };
        return value.Drives.All(d => d is not null && d.Device is not null && d.Device.Length == 2 && d.Device[0] is >= 'D' and <= 'Z' && d.Device[1] == ':' && targets.Contains(d.Target, StringComparer.OrdinalIgnoreCase));
    }
    public static void RecoverOrphans(string? journalRoot = null)
    {
        var root = Path.GetFullPath(journalRoot ?? Root);
        Directory.CreateDirectory(root);
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Папка состояния компилятора не должна быть ссылкой.");
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            try
            {
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(file).Length > 16000) continue;
                // An orphan scan must not deny atomic replacement of a live lease.
                // The open handle sees a complete old/new file, never a partial write.
                Record? value;
                using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    value = JsonSerializer.Deserialize<Record>(input);
                if (value is null || !Valid(value, file) || !value.Closed && Alive(value)) continue;
                if (Cleanup(value, recovering: true)) File.Delete(file);
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException) { /* Keep unverified records for inspection. */ }
        }
    }
    static bool Cleanup(Record value, bool recovering)
    {
        var removed = true;
        foreach (var drive in value.Drives) removed &= CompilerDrive.Release(drive.Device, drive.Target);
        if (value.ProfileCreated || recovering)
        {
            var hr = DeleteAppContainerProfile(Prefix + value.Id);
            removed &= hr >= 0 || hr == unchecked((int)0x80070002);
        }
        return removed;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        record = record with { Closed = true };
        try { Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { if (Cleanup(record, recovering: false)) File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Retain record for recovery. */ }
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] static extern int DeleteAppContainerProfile(string name);
}
