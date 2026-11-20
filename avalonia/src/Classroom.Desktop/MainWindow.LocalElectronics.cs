using Avalonia.Controls;
using Classroom.Desktop.Electronics;

namespace Classroom.Desktop;

public sealed partial class MainWindow
{
    internal async Task ShowFreeLocalCopies(string id,ElectronicsHistoryContext? recovery=null,string? problem=null)
    {
        if(api?.ProfileId is not { }profile||api.LessonId is not { }lesson||login?.Role!="student")throw new InvalidDataException("Сначала войдите в ученический профиль.");
        var client=api;var token=client.Token;var context=new ElectronicsHistoryContext(profile,client.Connection.Fingerprint,lesson,null,id);context.Validate();
        ClosePage();var origin=body.Content;var directory=Path.Combine(StudentDataDirectory,"electronics");
        bool Bound()=>ReferenceEquals(api,client)&&client.Token==token&&client.LessonId==lesson;
        var paused=new List<ElectronicsScreen>();ElectronicsLocalCopiesView? view=null;
        await StopElectronicsDelivery();
        try
        {
            if(!Bound()||!ReferenceEquals(body.Content,origin))return;
            foreach(var editor in freeElectronics.Values.Where(e=>e.FreeProjectId==id&&!e.IsDisposed).Distinct().ToArray())
            {if(!Bound())break;if(editor.IsDisposed)continue;paused.Add(editor);await editor.PauseDelivery();}
            if(!Bound()||!ReferenceEquals(body.Content,origin))return;
            if(recovery is not null)
            {
                if(recovery.ProfileId!=profile||recovery.Fingerprint!=context.Fingerprint||recovery.FreeProjectId!=id)throw new InvalidDataException("Копия относится к другому профилю.");
                foreach(var pair in freeElectronics.Where(p=>p.Value.HistoryContext?.LessonId==recovery.LessonId&&p.Value.FreeProjectId==id).ToArray())
                {
                    var editor=pair.Value;editor.Dispose();freeElectronics.Remove(pair.Key);
                    if(ReferenceEquals(electronics,editor))electronics=null;
                    if(ReferenceEquals(screen,editor))
                    {
                        var picker=new ElectronicsProjectPicker(this,client,OpenFreeProject,()=>EnterLesson(new("electronics"),legacyElectronics:true));
                        screen=picker;body.Content=picker;
                    }
                }
            }
            view=new ElectronicsLocalCopiesView(this,offset=>ElectronicsCopySelection.LocalPage(directory,context,offset),entry=>
            {
                var opened=paused.FirstOrDefault(e=>!e.IsDisposed&&string.Equals(e.SavePath,entry.ProjectFile,StringComparison.OrdinalIgnoreCase));
                return opened is null?ElectronicsCopySelection.InspectLocal(entry):new(entry,opened.CurrentProjectJson,"Копия открыта в редакторе. Показано его сохранённое состояние; просмотр не отправляет данные.");
            },async json=>
            {
                if(!Bound()||view!.IsDisposed)return;
                await CopyFreeProject(json,()=>Bound()&&!view.IsDisposed);
            },candidate=>Safe(async()=>
            {
                if(!Bound()||view!.IsDisposed)return;
                if(candidate.Entry.Context.ProfileId!=profile||candidate.Entry.Context.Fingerprint!=context.Fingerprint||candidate.Entry.Context.FreeProjectId!=id)
                    throw new InvalidDataException("Выбранная копия относится к другому профилю или проекту.");
                ElectronicsCopySelection.RestoreLocalChoice(candidate);
                Message("Выбор восстановлен. Схема и сохранённые результаты продолжают использовать исходные номера.");
                ClosePage();
                if(candidate.Entry.Context.LessonId==lesson)await OpenFreeProject(id,restoreLocal:true);
                else await EnterLesson(new("electronics"));
            }),problem,lesson);
            ShowPage("Локальные копии схемы",view,()=>
            {
                view.Dispose();foreach(var editor in paused)editor.ResumeDelivery();
                electronicsRecoveryOpen=false;StartElectronicsDelivery();
                body.IsEnabled=false;lessonBlock.IsVisible=true;lessonMessage.Text="Проверяем доступ к занятию…";
            });
            electronicsRecoveryOpen=true;body.IsEnabled=true;lessonBlock.IsVisible=false;
        }
        finally
        {
            if(view is null||view.IsDisposed)
            {foreach(var editor in paused)editor.ResumeDelivery();StartElectronicsDelivery();}
        }
    }
}
