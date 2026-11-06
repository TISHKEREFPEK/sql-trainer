using Avalonia.Controls;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed partial class StudentScreen
{
    void RefreshSaveStatus()
    {
        if (current is null || disposed) return;
        var work = local.Find(lessonId!, workScope, current.Id);
        if (work is not null && work.Code != editor.Text)
        {
            resolveConflict.IsVisible = false; retrySave.IsVisible = true;
            saved.Text = "Последние изменения не сохранены на ПК. Повторите сохранение или сохраните код отдельно.";
            return;
        }
        resolveConflict.IsVisible = work?.Conflict is not null;
        retrySave.IsVisible = work?.Conflict is null && (work?.Dirty == true || work?.PendingDraft is not null || work?.Error is not null || work?.Actions?.Length > 0);
        saved.Text = work?.Conflict is not null ? "На ПК и у преподавателя разные черновики. Выберите нужный вариант."
            : work?.Error is { } error ? "Сохранено на ПК; передача не завершена. " + error
            : work?.Dirty == true || work?.PendingDraft is not null ? "Сохранено на ПК, ожидает передачи."
            : work?.Task.Draft.Length > 0 ? "Черновик сохранён у преподавателя и на ПК." : "Работа сохранена на ПК.";
        if (work?.Execution is not null) saved.Text += " Есть незавершённый запрос: следующий запуск повторит прежний SQL.";
        if (work?.Actions?.Length > 0) saved.Text += " События ожидают передачи.";
    }
    Task ShowDraftConflict()
    {
        if (current is null) return Task.CompletedTask;
        var id = current.Id; var scope = workScope; var work = local.Find(lessonId!, scope, id);
        if (work?.Conflict is not { } conflict) return Task.CompletedTask;
        var mine = Ui.Input(work.Code, true); mine.IsReadOnly = true; mine.MaxHeight = 240;
        var remote = Ui.Input(conflict.ServerCode, true); remote.IsReadOnly = true; remote.MaxHeight = 240;
        async Task Choose(bool keepLocal)
        {
            local.Resolve(lessonId!, scope, id, keepLocal); owner.ClosePage();
            if (current?.Id == id && workScope == scope)
            {
                loading = true; editor.Text = local.Find(lessonId!, scope, id)!.Code; loading = false;
                RefreshSaveStatus(); await SaveDraft();
            }
        }
        owner.ShowPage("Конфликт черновика", Ui.Scroll(Ui.Stack(Ui.Text("Выберите вариант для продолжения. Оба текста сохранятся на этом ПК."), Ui.Text("На этом ПК", 18), mine,
            Ui.Text("У преподавателя", 18), remote, Ui.Row(Ui.Button("Оставить вариант с этого ПК", () => owner.Safe(() => Choose(true))), Ui.Button("Взять вариант у преподавателя", () => owner.Safe(() => Choose(false)))))));
        return Task.CompletedTask;
    }
}
