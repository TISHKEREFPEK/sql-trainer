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
        var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = Task.Run(() => { started.SetResult(); if (OperatingSystem.IsWindows()) lease.MarkProfile(); });
        await started.Task; await Task.Delay(150); Assert.False(write.IsCompleted); reader.Dispose(); await write;
        var record = System.Text.Json.JsonSerializer.Deserialize<CompilerLease.Record>(File.ReadAllText(file))!;
        Assert.True(record.ProfileCreated); Assert.False(record.Closed);
        var scanning = Task.Run(() => { if (OperatingSystem.IsWindows()) for (var i = 0; i < 100; i++) CompilerLease.RecoverOrphans(root); });
        for (var i = 0; i < 30; i++) await Task.Run(lease.MarkProfile);
        await scanning; Assert.True(File.Exists(file)); Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
    }
}
