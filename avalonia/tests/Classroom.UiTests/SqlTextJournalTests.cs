using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Classroom.Contracts;
using Classroom.Desktop;
using Xunit;

namespace Classroom.UiTests;

public sealed class SqlTextJournalTests
{
    [Fact]
    public void UnicodeEditsUndoAndCheckpointsReplayEveryIntermediateText()
    {
        var texts = new List<string> { "SELECT '😀' AS имя;" }; var journal = SqlTextJournal.Start(texts[0], 1);
        for (var i = 1; i <= 200; i++)
        {
            var next = i % 3 == 0 ? "SELECT '😃' AS имя;" : "SELECT '😀' AS имя;\n-- шаг " + i;
            journal = SqlTextJournal.Record(journal, texts[^1], next, "student", "edit", i + 1); texts.Add(next);
        }
        for (var i = 0; i < texts.Count; i++) Assert.Equal(texts[i], journal.Replay(i));
        Assert.Equal(3, journal.Changes.Count(c => c.Checkpoint is not null));
        var restored = Wire.Read<SqlTextJournal>(Wire.Write(journal)); Assert.Equal(texts[^1], restored.Replay());
        var changes = restored.Changes.ToArray(); changes[0] = changes[0] with { Offset = int.MaxValue };
        Assert.Throws<InvalidDataException>(() => (restored with { Changes = changes }).Replay());
        Assert.Throws<InvalidDataException>(() => SqlTextJournal.Record(null, "SELECT 1;", "SELECT '\uD800';", "student", "edit", 1));
        changes = restored.Changes.ToArray(); changes[1] = changes[1] with { Sequence = 1 };
        Assert.Throws<InvalidDataException>(() => (restored with { Changes = changes }).Replay());
    }
    [AvaloniaFact]
    public async Task NativeEditsAndRealUndoSurviveReopeningTheLocalStore()
    {
        await using var f = await LiveClassroom.Start(); var root = Path.Combine(Path.GetTempPath(), "classroom-sql-journal-" + Guid.NewGuid().ToString("N"));
        var window = new MainWindow(false, false, root); using var view = new StudentScreen(window, f.Student);
        window.Preview(view); window.Show(); await view.Load("database"); window.UpdateLayout();
        var editor = view.GetVisualDescendants().OfType<TextEditor>().Single(); var states = new List<(int Step, string Code)>();
        void Capture() => states.Add((window.SqlStore(f.Student).Find(f.Student.LessonId!, "course", "database")!.Journal!.Changes.Length, editor.Text));
        Capture();
        try
        {
            editor.Document.UndoStack.ClearAll();
            editor.Document.Insert(editor.Document.TextLength, "\n-- первый шаг 😀"); Capture();
            editor.Document.Insert(editor.Document.TextLength, "\n-- второй шаг"); Capture();
            ApplicationCommands.Undo.Execute(null, editor.TextArea); Capture();
            ApplicationCommands.Redo.Execute(null, editor.TextArea); Capture();
            var work = window.SqlStore(f.Student).Find(f.Student.LessonId!, "course", "database")!;
            Assert.NotNull(work.Journal); Assert.Equal(new[] { "edit", "edit", "undo", "redo" }, work.Journal.Changes.TakeLast(4).Select(c => c.Kind));
            foreach (var state in states) Assert.Equal(state.Code, work.Journal.Replay(state.Step));
        }
        finally { view.Dispose(); window.Close(); }
        using var reopened = new SqlLocalStore(root, f.Student.Connection.Fingerprint, f.StudentId);
        var history = reopened.Find(f.Student.LessonId!, "course", "database")!.Journal!;
        foreach (var state in states) Assert.Equal(state.Code, history.Replay(state.Step));
    }
}
