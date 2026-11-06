using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    string codeHistoryKind = "code-edit";
    bool historyVisible;
    internal Classroom.Contracts.ElectronicsJournal? LocalHistory => history?.Journal;
    void RecordHistory(string kind, bool force = false, string? gestureId = null, string? reportId = null, string? reportHash = null, Classroom.Contracts.ElectronicsSerialIo? serialIo = null,Classroom.Contracts.ElectronicsI2cIo[]? i2cIo = null)
    {
        if (history is null || disposed) return;
        project.Validate(); var json = project.Serialize(); var changed = history.ProjectJson != json;
        history.Record(json, kind, force, gestureId, reportId, reportHash,serialIo,i2cIo);
        // An in-flight old ACK must not label a newly recorded edit as saved remotely.
        if (changed) sync?.QueueDraft(json);
    }
    void CodeCommand(string kind, Action action)
    {
        var previous = codeHistoryKind; codeHistoryKind = kind;
        try { action(); } finally { codeHistoryKind = previous; }
    }
    void ConfigureHistory()
    {
        editor.TextArea.CommandBindings.Insert(0, new RoutedCommandBinding(ApplicationCommands.Undo, (_, e) =>
        { e.Handled = true; CodeCommand("code-undo", () => editor.Document.UndoStack.Undo()); }, (_, e) => e.CanExecute = !editor.IsReadOnly && editor.Document.UndoStack.CanUndo));
        editor.TextArea.CommandBindings.Insert(0, new RoutedCommandBinding(ApplicationCommands.Redo, (_, e) =>
        { e.Handled = true; CodeCommand("code-redo", () => editor.Document.UndoStack.Redo()); }, (_, e) => e.CanExecute = !editor.IsReadOnly && editor.Document.UndoStack.CanRedo));
        AttachedToVisualTree += (_, _) => { historyVisible = true; _ = Perform(() => RecordHistory("work-open", true)); };
        DetachedFromVisualTree += (_, _) => { if (!disposed && historyVisible) _ = Perform(() => RecordHistory("work-leave", true)); historyVisible = false; };
        owner.Activated += HistoryActivated; owner.Deactivated += HistoryDeactivated; owner.PropertyChanged += HistoryWindowState;
    }
    void HistoryActivated(object? sender, EventArgs e) { if (historyVisible) _ = Perform(() => RecordHistory("window-focus", true)); }
    void HistoryDeactivated(object? sender, EventArgs e) { if (historyVisible) _ = Perform(() => RecordHistory("window-blur", true)); }
    void HistoryWindowState(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!historyVisible || e.Property != Window.WindowStateProperty) return;
        if (owner.WindowState == WindowState.Minimized) _ = Perform(() => RecordHistory("window-minimize", true));
        else if (e.OldValue is WindowState.Minimized) _ = Perform(() => RecordHistory("window-restore", true));
    }
    void ReleaseHistory()
    {
        if (historyVisible) _ = Perform(() => RecordHistory("work-close", true)); historyVisible = false;
        owner.Activated -= HistoryActivated; owner.Deactivated -= HistoryDeactivated; owner.PropertyChanged -= HistoryWindowState;
    }
}
