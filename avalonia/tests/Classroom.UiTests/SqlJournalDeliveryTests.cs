using Classroom.Contracts;
using Classroom.Desktop;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlJournalDeliveryTests
{
    static string Root() => Path.Combine(Path.GetTempPath(), "classroom-journal-delivery-" + Guid.NewGuid().ToString("N"));
    static SqlLocalStore Store(string root, LiveClassroom f) => new(root, f.Student.Connection.Fingerprint, f.StudentId);
    [Fact]
    public async Task LocalPagesReachTeacherInOrderAndReopenDoesNotDuplicateSteps()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!; var root = Root();
        SqlTextJournal history;
        using (var local = Store(root, f))
        {
            local.Observe(lesson, task);
            for (var i = 0; i < 170; i++) local.Edit(lesson, task.WorkScope, task.Id, $"SELECT {i}; -- текст 😀", 4);
            history = local.Find(lesson, task.WorkScope, task.Id)!.Journal!;
            await local.Drain(f.Student);
            Assert.Equal(170, local.Find(lesson, task.WorkScope, task.Id)!.JournalDelivery!.Acknowledged);
            Assert.False(local.Find(lesson, task.WorkScope, task.Id)!.Dirty);
        }
        using (var reopened = Store(root, f)) { await reopened.Drain(f.Student); await reopened.Drain(f.Student); }
        var stream = Assert.Single(await f.Admin.Get<SqlJournalStreamInfo[]>($"teacher/sql-journal/{f.StudentId}"));
        Assert.Equal(170, stream.Sequence);
        foreach (var sequence in new[] { 0, 1, 63, 64, 65, 128, 170 })
            Assert.Equal(history.Replay(sequence), (await f.Admin.Get<SqlJournalText>($"teacher/sql-journal/{f.StudentId}/{stream.Id}/state?sequence={sequence}")).Code);
    }
    [Fact]
    public async Task RestoredOlderAcknowledgedCopyForksWithoutReplacingAcceptedHistory()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!; var root = Root();
        string file, older, original;
        using (var local = Store(root, f))
        {
            local.Observe(lesson, task); local.Edit(lesson, task.WorkScope, task.Id, "SELECT 1;", 0); await local.Drain(f.Student);
            file = Assert.Single(Directory.GetFiles(root, "*.work.json", SearchOption.AllDirectories)); older = File.ReadAllText(file);
            original = local.Find(lesson, task.WorkScope, task.Id)!.Journal!.StreamId;
            local.Edit(lesson, task.WorkScope, task.Id, "SELECT 2;", 0); local.Edit(lesson, task.WorkScope, task.Id, "SELECT 3;", 0); await local.Drain(f.Student);
        }
        File.WriteAllText(file, older);
        string fork;
        using (var restored = Store(root, f))
        {
            restored.Edit(lesson, task.WorkScope, task.Id, "SELECT 200;", 0); await restored.Drain(f.Student);
            var work = restored.Find(lesson, task.WorkScope, task.Id)!; fork = work.JournalDelivery!.StreamId;
            Assert.NotEqual(original, fork); Assert.Equal(original, work.JournalDelivery.ForkedFrom);
            Assert.Equal(2, work.JournalDelivery.Acknowledged); Assert.NotNull(work.Conflict);
        }
        using (var reopened = Store(root, f)) await reopened.Drain(f.Student);
        Assert.Equal(2, (await f.Admin.Get<SqlJournalStreamInfo[]>($"teacher/sql-journal/{f.StudentId}")).Length);
        Assert.Equal("SELECT 3;", (await f.Admin.Get<SqlJournalText>($"teacher/sql-journal/{f.StudentId}/{original}/state?sequence=3")).Code);
        Assert.Equal("SELECT 200;", (await f.Admin.Get<SqlJournalText>($"teacher/sql-journal/{f.StudentId}/{fork}/state?sequence=2")).Code);
    }
    [Fact]
    public async Task MatchingFinalTextDoesNotHideDivergentEventIdsAfterRestore()
    {
        await using var f = await LiveClassroom.Start(); var task = await f.Student.Get<StudentTask>("tasks/database"); var lesson = f.Student.LessonId!; var root = Root();
        string file, older;
        using (var local = Store(root, f))
        {
            local.Observe(lesson, task); local.Edit(lesson, task.WorkScope, task.Id, "SELECT 1;", 0); await local.Drain(f.Student);
            file = Assert.Single(Directory.GetFiles(root, "*.work.json", SearchOption.AllDirectories)); older = File.ReadAllText(file);
            local.Edit(lesson, task.WorkScope, task.Id, "SELECT 2;", 0); await local.Drain(f.Student);
        }
        File.WriteAllText(file, older);
        using (var restored = Store(root, f))
        {
            restored.Edit(lesson, task.WorkScope, task.Id, "SELECT 2;", 0); await restored.Drain(f.Student);
            Assert.NotNull(restored.Find(lesson, task.WorkScope, task.Id)!.JournalDelivery!.ForkedFrom);
        }
        var streams = await f.Admin.Get<SqlJournalStreamInfo[]>($"teacher/sql-journal/{f.StudentId}"); Assert.Equal(2, streams.Length);
        var ids = new List<string>();
        foreach (var stream in streams)
        {
            Assert.Equal(2, stream.Sequence);
            ids.Add((await f.Admin.Get<SqlJournalEvents>($"teacher/sql-journal/{f.StudentId}/{stream.Id}/changes")).Events[^1].Change.Id);
        }
        Assert.NotEqual(ids[0], ids[1]);
    }
}
