using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed partial class StudentScreen
{
    string editKind = "edit";
    bool historyVisible;
    bool historyFocused;
    void RecordHistory(string kind, SqlJournalReference? reference = null)
    {
        if (disposed || current is null || local is null) return;
        try { local.Mark(lessonId!, workScope, current.Id, kind, reference); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        { saved.Text = "Не удалось сохранить событие на ПК: " + e.Message; owner.Error(saved.Text); }
    }
    void TextCommand(string kind, Action action)
    {
        var previous = editKind; editKind = kind;
        var length = current is null ? 0 : local.Find(lessonId!, workScope, current.Id)?.Journal?.Changes.Length ?? 0;
        try
        {
            action();
            if (current is not null && local.Find(lessonId!, workScope, current.Id)?.Journal?.Changes.Length == length) RecordHistory(kind);
        }
        finally { editKind = previous; }
    }
    void ConfigureHistory()
    {
        editor.TextArea.CommandBindings.Insert(0, new RoutedCommandBinding(ApplicationCommands.Undo, (_, e) =>
        { e.Handled = true; TextCommand("undo", () => editor.Document.UndoStack.Undo()); },
            (_, e) => e.CanExecute = !editor.IsReadOnly && editor.Document.UndoStack.CanUndo));
        editor.TextArea.CommandBindings.Insert(0, new RoutedCommandBinding(ApplicationCommands.Redo, (_, e) =>
        { e.Handled = true; TextCommand("redo", () => editor.Document.UndoStack.Redo()); },
            (_, e) => e.CanExecute = !editor.IsReadOnly && editor.Document.UndoStack.CanRedo));
        AttachedToVisualTree += (_, _) =>
        {
            if (disposed) return;
            historyVisible = true; historyFocused = owner.IsActive;
            RecordHistory("work-open");
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (!historyVisible) return;
            RecordHistory("work-leave"); historyVisible = false;
        };
        owner.Activated += HistoryActivated; owner.Deactivated += HistoryDeactivated; owner.PropertyChanged += HistoryWindowState; owner.Closed += HistoryClosed;
    }
    void HistoryActivated(object? sender, EventArgs e)
    {
        if (!historyVisible || historyFocused) return;
        historyFocused = true; RecordHistory("window-focus");
    }
    void HistoryDeactivated(object? sender, EventArgs e)
    {
        if (!historyVisible || !historyFocused) return;
        historyFocused = false; RecordHistory("window-blur");
    }
    void HistoryWindowState(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!historyVisible || e.Property != Window.WindowStateProperty) return;
        if (owner.WindowState == WindowState.Minimized) RecordHistory("window-minimize");
        else if (e.OldValue is WindowState.Minimized) RecordHistory("window-restore");
    }
    void ReleaseHistory()
    {
        historyVisible = false;
        owner.Activated -= HistoryActivated; owner.Deactivated -= HistoryDeactivated; owner.PropertyChanged -= HistoryWindowState; owner.Closed -= HistoryClosed;
    }
    void HistoryClosed(object? sender, EventArgs e) => ReleaseHistory();
}
