using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsBackgroundDeliveryTests
{
    static string Root() => Path.Combine(Path.GetTempPath(), "classroom-background-" + Guid.NewGuid().ToString("N"));
    static string ProjectPath(string root, ClassroomApi api, string? assignment = null) => Path.Combine(root, "electronics",
        SqlSyncHash.Text(api.Connection.Fingerprint + ":" + api.ProfileId) + (assignment is null ? "" : "-" + assignment) + ".json");
    static void SaveClosed(string path, ClassroomApi api, string lesson, string? assignment = null, int changes = 1, string? report = null, int? version = null)
    {
        var project = CircuitProject.Blink(); var initial = project.Serialize();
        using var history = new ElectronicsHistoryStore(path + ".history", new(api.ProfileId!, api.Connection.Fingerprint, lesson, assignment), initial);
        for (var i = 1; i <= changes; i++) { project.Code = "// закрытая работа " + i; history.Record(project.Serialize(), "code-edit"); }
        var json = project.Serialize(); ElectronicsHistoryStore.Mirror(path, json);
        using var sync = new ElectronicsSync(api, path + ".sync.json", _ => { }, startTimer: false, assignmentId: assignment);
        sync.QueueDraft(json);
        if (report is not null)
        {
            history.Record(json, "report-submitted", true, reportId: report, reportHash: SqlSyncHash.Text(json));
            sync.Record("submitted", json, expectedAssignmentVersion: version, reportId: report);
        }
    }
    static async Task Drain(ElectronicsBackgroundDelivery queue, LiveClassroom f, int streams, long events)
    {
        for (var i = 0; i < 40; i++)
        {
            Assert.Null((await queue.DrainOnce()).Error); var saved = await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}");
            if (saved.Length == streams && saved.Sum(s => s.Sequence) == events) return;
        }
        var actual = await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}");
        Assert.Equal(streams, actual.Length); Assert.Equal(events, actual.Sum(s => s.Sequence));
    }
    [Fact]
    public async Task ClosedAssignedWorkAndEveryFreeLessonContextResumeInSqlOnlyLesson()
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var lesson = f.Student.LessonId!; var task = Guid.NewGuid().ToString("N");
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + task, new ElectronicsTaskRequest("Старая схема", "Условие"));
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson}/electronics-tasks", new ElectronicsSelectionRequest([task]));
        var assigned = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + task); var report = Guid.NewGuid().ToString("N");
        SaveClosed(ProjectPath(root, f.Student, assigned.Id), f.Student, lesson, assigned.Id, 130, report, assigned.StateVersion);
        var free = ProjectPath(root, f.Student); SaveClosed(free, f.Student, lesson);
        await f.Admin.Post<LessonView>($"teacher/lessons/{lesson}/finish");
        var second = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, OpenToAllGroups: true, FreePractice: true)));
        await f.Admin.Post<LessonView>($"teacher/lessons/{second.Id}/start"); await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        SaveClosed(free, f.Student, second.Id, changes: 2);
        await f.Admin.Post<LessonView>($"teacher/lessons/{second.Id}/finish");
        var third = await f.Admin.Post<LessonView>("teacher/lessons", new CreateLessonRequest(Guid.NewGuid().ToString("N"), new(null, AllowElectronics: false, OpenToAllGroups: true)));
        await f.Admin.Post<LessonView>($"teacher/lessons/{third.Id}/start"); await f.Student.Login(new("student", "ui-test", "synthetic-student-password"));
        await using (var queue = new ElectronicsBackgroundDelivery(root, f.Student)) await Drain(queue, f, 3, 134);
        var records = await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}");
        Assert.Equal(2, records.Select(s => s.LessonId).Distinct().Count()); Assert.DoesNotContain(records, s => s.LessonId == third.Id);
        var assignedRecord = Assert.Single(records, s => s.AssignmentId == assigned.Id);
        var link = await f.Admin.Get<ElectronicsJournalReportLink>($"teacher/electronics-journal/{f.StudentId}/{assignedRecord.Id}/changes/131/report");
        Assert.Equal("available", link.Availability); Assert.Equal(report, link.ReportId);
        await using (var restarted = new ElectronicsBackgroundDelivery(root, f.Student)) for (var i = 0; i < 6; i++) await restarted.DrainOnce();
        var after = await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}");
        Assert.Single(after.Reports); Assert.Equal(report, after.Reports[0].Id);
        Assert.Equal(134, (await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}")).Sum(s => s.Sequence));
    }
    [Fact]
    public async Task BrokenQueueIsPreservedAndDoesNotStopAnotherClosedWork()
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var task = Guid.NewGuid().ToString("N"); var lesson = f.Student.LessonId!;
        await f.Admin.Put<ElectronicsTaskDto>("teacher/electronics/tasks/" + task, new ElectronicsTaskRequest("Схема", "Условие"));
        await f.Admin.Put<ElectronicsSelection>($"teacher/lessons/{lesson}/electronics-tasks", new ElectronicsSelectionRequest([task]));
        var assigned = await f.Student.Get<ElectronicsAssignmentDto>("electronics/tasks/" + task); var broken = ProjectPath(root, f.Student, assigned.Id);
        SaveClosed(broken, f.Student, lesson, assigned.Id); File.WriteAllText(broken + ".sync.json", "{broken");
        var originalIndex = File.ReadAllBytes(Assert.Single(Directory.GetFiles(broken + ".history", "c-*.json")));
        SaveClosed(ProjectPath(root, f.Student), f.Student, lesson);
        await using var queue = new ElectronicsBackgroundDelivery(root, f.Student); var errors = new List<string>();
        for (var i = 0; i < 6; i++) if ((await queue.DrainOnce()).Error is { } error) errors.Add(error);
        Assert.NotEmpty(errors); Assert.Equal("{broken", File.ReadAllText(broken + ".sync.json"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(broken)!, "*.corrupt-*"));
        Assert.Equal(originalIndex, File.ReadAllBytes(Assert.Single(Directory.GetFiles(broken + ".history", "c-*.json"))));
        Assert.Null(Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}")).AssignmentId);
    }
    [Fact]
    public async Task ChangedClientBindingCannotUploadANewCopyBeforeHistoryValidation()
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var path = ProjectPath(root, f.Student); var lesson = f.Student.LessonId!;
        SaveClosed(path, f.Student, lesson);
        await using (var queue = new ElectronicsBackgroundDelivery(root, f.Student)) await Drain(queue, f, 1, 1);
        var outbox = Wire.Read<ElectronicsSync.Outbox>(File.ReadAllText(path + ".sync.json")); outbox.ClientId = Guid.NewGuid().ToString("N");
        var changed = Wire.Write(outbox); File.WriteAllText(path + ".sync.json", changed);
        await using var retry = new ElectronicsBackgroundDelivery(root, f.Student); var result = await retry.DrainOnce();
        Assert.False(result.Complete); Assert.Contains("привязку", result.Error); Assert.Equal(changed, File.ReadAllText(path + ".sync.json"));
        Assert.Single((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOutboxOrForeignContextDoesNotCreateAReplacement(bool missingOutbox)
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var path = ProjectPath(root, f.Student); var lesson = f.Student.LessonId!;
        if (missingOutbox)
        {
            using var original = new ElectronicsHistoryStore(path + ".history", new(f.StudentId, f.Student.Connection.Fingerprint, lesson, null), CircuitProject.Blink().Serialize());
        }
        else
        {
            using var foreign = new ElectronicsHistoryStore(path + ".history", new(Guid.NewGuid().ToString("N"), f.Student.Connection.Fingerprint, lesson, null), CircuitProject.Blink().Serialize());
            File.WriteAllText(path + ".sync.json", "do not read or replace");
        }
        var index = Assert.Single(Directory.GetFiles(path + ".history", "c-*.json")); var before = File.ReadAllBytes(index);
        await using var queue = new ElectronicsBackgroundDelivery(root, f.Student); var result = await queue.DrainOnce();
        Assert.False(result.Complete); Assert.NotNull(result.Error); Assert.Equal(before, File.ReadAllBytes(index));
        if (missingOutbox) Assert.False(File.Exists(path + ".sync.json")); else Assert.Equal("do not read or replace", File.ReadAllText(path + ".sync.json"));
        Assert.Empty((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works);
        Assert.Single(Directory.GetFiles(path + ".history", "c-*.json"));
    }
    [Fact]
    public async Task HeldEditorLeaseAndOtherProfileAreSkippedAndCancellationReleasesFiles()
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var path = ProjectPath(root, f.Student); var context = new ElectronicsHistoryContext(f.StudentId, f.Student.Connection.Fingerprint, f.Student.LessonId!, null);
        SaveClosed(path, f.Student, context.LessonId, changes: 130);
        await using (var queue = new ElectronicsBackgroundDelivery(root, f.Student))
        {
            using (var editor = new ElectronicsHistoryStore(path + ".history", context, null)) await queue.DrainOnce();
            Assert.Empty(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}"));
            var pending = queue.DrainOnce(); await queue.DisposeAsync(); await pending;
        }
        using (var unlocked = new ElectronicsHistoryStore(path + ".history", context, null)) Assert.Equal(130, unlocked.Journal.Changes.Length);
        await using (var resumed = new ElectronicsBackgroundDelivery(root, f.Student)) await Drain(resumed, f, 1, 130);
        var strangerId = Guid.NewGuid().ToString("N"); var strangerPath = Path.Combine(root, "electronics", SqlSyncHash.Text(f.Student.Connection.Fingerprint + ":" + strangerId) + ".json");
        using (var stranger = new ElectronicsHistoryStore(strangerPath + ".history", context with { ProfileId = strangerId }, CircuitProject.Blink().Serialize())) { }
        await using (var queue = new ElectronicsBackgroundDelivery(root, f.Student)) Assert.True((await queue.DrainOnce()).Complete);
        await using var oldSession = new ElectronicsBackgroundDelivery(root, f.Student);
        await f.Student.Login(new("student", "ui-test", "synthetic-student-password")); Assert.False((await oldSession.DrainOnce()).Complete);
        Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}"));
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowTimerDeliversWithoutOpeningAnElectronicsEditor(bool legacy)
    {
        await using var f = await LiveClassroom.Start(); var root = Root(); var path = ProjectPath(root, f.Student);
        if (legacy) ElectronicsLegacyDeliveryTests.SaveLegacy(path, f.Student); else SaveClosed(path, f.Student, f.Student.LessonId!);
        var login = await f.Student.Login(new("student", "ui-test", "synthetic-student-password")); var window = new MainWindow(false, false, root);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetField("api", flags)!.SetValue(window, f.Student); typeof(MainWindow).GetField("login", flags)!.SetValue(window, login);
        using var sql = new StudentScreen(window, f.Student);
        typeof(MainWindow).GetField("student", flags)!.SetValue(window, sql); typeof(MainWindow).GetField("screen", flags)!.SetValue(window, sql);
        window.Preview(sql); window.Show(); await sql.Load();
        typeof(MainWindow).GetMethod("StartElectronicsDelivery", flags)!.Invoke(window, null);
        try
        {
            for (var i = 0; i < 80 && (await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works.Length == 0; i++) await Task.Delay(100);
            Assert.Single((await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works);
            if (legacy) Assert.Empty(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}"));
            else
            {
                for (var i = 0; i < 80 && (await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}")).Length == 0; i++) await Task.Delay(100);
                Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}"));
            }
            Assert.Empty(window.GetType().GetField("assignedElectronics", flags)!.GetValue(window) as System.Collections.IDictionary ?? throw new InvalidOperationException());
            Assert.Null(typeof(MainWindow).GetField("electronics", flags)!.GetValue(window));
            var expectedCode = CircuitProject.Parse(File.ReadAllText(path)).Code;
            await window.EnterLesson(new("electronics"));
            window.UpdateLayout();
            var circuit = Assert.IsType<ElectronicsScreen>(typeof(MainWindow).GetField("electronics", flags)!.GetValue(window));
            Assert.Equal(expectedCode, circuit.GetVisualDescendants().OfType<TextEditor>().Single().Text);
        }
        finally { await (Task)typeof(MainWindow).GetMethod("Quit", flags)!.Invoke(window, null)!; }
    }
}
