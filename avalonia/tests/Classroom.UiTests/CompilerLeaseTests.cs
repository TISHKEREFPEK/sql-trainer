using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class CompilerLeaseTests
{
    [Fact]
    public async Task BriefReaderLockDoesNotAbortAtomicLeaseUpdateAndRecoveryKeepsLiveOwner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "classroom-lease-write-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var lease = new CompilerLease(Path.Combine(root, "runtime"), Path.Combine(root, "scratch"), root);
        var file = Assert.Single(Directory.GetFiles(root, "*.json"));
        using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var started = new ManualResetEventSlim(); using var finished = new ManualResetEventSlim(); Exception? failure = null;
        // This suite runs many live servers in parallel. An awaited timer on the
        // shared pool can resume after all bounded retries have already elapsed.
        // Keep the held-lock interval on this thread and the writer off that pool.
        var writer = new Thread(() =>
        {
            started.Set(); try { if (OperatingSystem.IsWindows()) lease.MarkProfile(); }
            catch (Exception e) { failure = e; }
            finally { finished.Set(); }
        }) { IsBackground = true };
        writer.Start();
        try { Assert.True(started.Wait(TimeSpan.FromSeconds(2))); Thread.Sleep(80); Assert.False(finished.IsSet); }
        finally { reader.Dispose(); Assert.True(writer.Join(TimeSpan.FromSeconds(5))); }
        Assert.Null(failure);
        var record = System.Text.Json.JsonSerializer.Deserialize<CompilerLease.Record>(File.ReadAllText(file))!;
        Assert.True(record.ProfileCreated); Assert.False(record.Closed);
        var scanning = Task.Run(() => { if (OperatingSystem.IsWindows()) for (var i = 0; i < 100; i++) CompilerLease.RecoverOrphans(root); });
        for (var i = 0; i < 30; i++) await Task.Run(lease.MarkProfile);
        await scanning; Assert.True(File.Exists(file)); Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
    }
}
