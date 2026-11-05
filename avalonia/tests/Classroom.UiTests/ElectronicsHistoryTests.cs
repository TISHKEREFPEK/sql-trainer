using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using System.Diagnostics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsHistoryTests
{
    static string Root() => Path.Combine(Path.GetTempPath(), "classroom-electronics-history-" + Guid.NewGuid().ToString("N"));
    static ElectronicsHistoryContext Context() => new(Guid.NewGuid().ToString("N"), new string('0', 64), Guid.NewGuid().ToString("N"), null);
    [Fact]
    public void ImmutablePagesRestoreCodeSchemeAndEveryIntermediateGestureAfterReopening()
    {
        var root = Root(); var context = Context(); var project = CircuitProject.Blink(); var states = new List<string> { project.Serialize() };
        var gesture = Guid.NewGuid().ToString("N"); string page; var sentinel = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var store = new ElectronicsHistoryStore(root, context, states[0]))
        {
            Assert.Throws<IOException>(() => new ElectronicsHistoryStore(root, context, states[0]));
            for (var i = 1; i <= 64; i++)
            {
                project.Code = "// шаг " + i + " 😀\nvoid setup() {}\nvoid loop() {}";
                project.Parts[1] = project.Parts[1] with { X = 350 + i, Angle = i };
                states.Add(project.Serialize()); store.Record(states[^1], "move", gestureId: gesture);
            }
            page = Assert.Single(Directory.GetFiles(root, "p-*.json")); File.SetLastWriteTimeUtc(page, sentinel);
            for (var i = 65; i <= 130; i++)
            {
                project.Parts[1] = project.Parts[1] with { StretchX = 1 + i / 100d }; states.Add(project.Serialize()); store.Record(states[^1], "resize", gestureId: gesture);
            }
            Assert.Equal(sentinel, File.GetLastWriteTimeUtc(page)); Assert.Equal(2, Directory.GetFiles(root, "p-*.json").Length);
            Assert.All(store.Journal.Changes, c => Assert.Equal(gesture, c.GestureId));
        }
        using (var reopened = new ElectronicsHistoryStore(root, context, CircuitProject.Blink().Serialize()))
        {
            Assert.True(reopened.Reopened); Assert.Equal(states[^1], reopened.ProjectJson);
            for (var i = 0; i < states.Count; i++) Assert.Equal(states[i], reopened.Journal.Replay(i));
        }
        File.AppendAllText(page, "tampered");
        Assert.Throws<InvalidDataException>(() => new ElectronicsHistoryStore(root, context, states[0])); Assert.EndsWith("tampered", File.ReadAllText(page));
    }
    [Fact]
    public void FailedPointerWriteAndCorruptionNeverReplaceTheLastVerifiedHistory()
    {
        var root = Root(); var context = Context(); var project = CircuitProject.Blink(); var initial = project.Serialize(); string file;
        using (var store = new ElectronicsHistoryStore(root, context, initial))
        {
            file = Assert.Single(Directory.GetFiles(root, "c-*.json")); var bytes = File.ReadAllText(file); Directory.CreateDirectory(file + ".backup");
            project.Code += "\n// unsaved";
            var error = Assert.ThrowsAny<Exception>(() => store.Record(project.Serialize(), "code-edit")); Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(bytes, File.ReadAllText(file)); Assert.Equal(initial, store.ProjectJson); Assert.Empty(store.Journal.Changes);
        }
        File.WriteAllText(file, "{}");
        Assert.Throws<InvalidDataException>(() => new ElectronicsHistoryStore(root, context, initial)); Assert.Equal("{}", File.ReadAllText(file));
    }
    [Fact]
    public void UnicodeBoundariesAndReportLinksAreValidatedWithoutExecutingCode()
    {
        var project = CircuitProject.Blink(); project.Code = "// 😀";
        var before = project.Serialize().Replace("\\uD83D\\uDE00", "😀", StringComparison.Ordinal);
        var after = before.Replace("😀", "😃", StringComparison.Ordinal);
        var journal = ElectronicsJournal.Start(before, 1); journal = ElectronicsJournal.Record(journal, before, after, "code-edit", 2);
        Assert.Equal(after, journal.Replay());
        var report = Guid.NewGuid().ToString("N"); journal = ElectronicsJournal.Record(journal, after, after, "report-submitted", 3, force: true, reportId: report, reportHash: SqlSyncHash.Text(after));
        Assert.Equal(report, journal.Changes[^1].ReportId); Assert.Equal(after, journal.Replay());
        var changes = journal.Changes.ToArray(); changes[0] = changes[0] with { AfterHash = new string('0', 64) };
        Assert.Throws<InvalidDataException>(() => (journal with { Changes = changes }).Replay());
        Assert.Throws<InvalidDataException>(() => ElectronicsJournal.Record(journal, after, after, "report-submitted", 4, force: true, reportId: report));
        project.Code = "// \uD800"; Assert.Throws<InvalidDataException>(project.Validate);
    }
    [Fact]
    public async Task KilledWriterKeepsExactSchemeCodeAndSubmissionIntentWithoutDispose()
    {
        var root = Root(); Directory.CreateDirectory(root); var context = Context(); var initial = CircuitProject.Blink(); var edited = CircuitProject.Parse(initial.Serialize());
        edited.Code += "\n// после сбоя 😀"; edited.Parts[1] = edited.Parts[1] with { Angle = 23.5, StretchY = 1.3 }; var report = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, "history"); var input = Path.Combine(root, "probe-input.json");
        await File.WriteAllTextAsync(input, Wire.Write(new { Root = directory, Context = context, InitialJson = initial.Serialize(), EditedJson = edited.Serialize(), ReportId = report }));
        var probe = Environment.GetEnvironmentVariable("CLASSROOM_TEST_SANDBOX_PROBE") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Classroom.SandboxProbe", "release", "Classroom.SandboxProbe.dll"));
        var start = new ProcessStartInfo("dotnet") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true }; start.ArgumentList.Add(probe); start.ArgumentList.Add("electronics-history"); start.ArgumentList.Add(input);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            for (var i = 0; i < 200 && !File.Exists(input + ".ready") && !process.HasExited; i++) await Task.Delay(25);
            Assert.True(File.Exists(input + ".ready"), process.HasExited ? await output + await errors : "History probe did not complete its durable write.");
            Assert.Throws<IOException>(() => new ElectronicsHistoryStore(directory, context, initial.Serialize()));
            process.Kill(true); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await process.WaitForExitAsync(timeout.Token);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        using var reopened = new ElectronicsHistoryStore(directory, context, initial.Serialize()); Assert.Equal(edited.Serialize(), reopened.ProjectJson);
        Assert.Equal(report, reopened.Journal.Changes[^1].ReportId); Assert.Equal(SqlSyncHash.Text(edited.Serialize()), reopened.Journal.Changes[^1].ReportHash);
        Assert.Equal(initial.Serialize(), reopened.Journal.Replay(0)); Assert.Equal(edited.Serialize(), reopened.Journal.Replay(1));
    }
}
