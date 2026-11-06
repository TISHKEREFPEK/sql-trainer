using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;

namespace Classroom.Desktop;

public sealed class ClassroomResultsView : UserControl, IDisposable
{
    sealed record Choice(string? Id, string Label) { public override string ToString() => Label; }
    sealed record Row(string Id, string Name, string Progress, string State, string Last, ResultsWork? Work = null);
    readonly MainWindow owner; readonly ClassroomApi api;
    readonly StackPanel page = Ui.Stack(); readonly ScrollViewer scroll;
    readonly Dictionary<string, Vector> positions = new();
    ResultsOverview? data; LessonDashboard? lessons; readonly List<LessonView> older = new();
    string mode = "lesson"; string? lessonId, group, studentId, subject; bool allStudents, initialized, loading, refreshPending, disposed;
    ResultsWork? selectedWork; ResultsSqlDetail? sqlDetail;
    ElectronicsReviewQueue? submissions;
    int generation; string Location => $"{mode}/{lessonId}/{group}/{studentId}/{selectedWork?.Subject}:{selectedWork?.TaskId}:{selectedWork?.LessonId}";
    public ClassroomResultsView(MainWindow owner, ClassroomApi api) { this.owner=owner;this.api=api;scroll=Ui.Scroll(page);Content=scroll; }
    static string Percent(double? value) => value is null ? "—" : value.Value.ToString("F1") + "%";
    static string Date(long? at) => at is { } time ? Ui.Time(time) : "—";
    static string State(ResultsWork w) => w.State switch { "not-started"=>"Не начато","working"=>"В работе","completed"=>"Решено","credited"=>"Зачтено прежнее", "awaiting-review"=>w.Completed?"На пересдаче · зачёт сохранён":"Ожидает проверки", "accepted"=>"Принято","changes-requested"=>"На доработку","rejected"=>"Не принято",_=>w.State };
    string Query(bool export=false)
    {
        var query="?mode="+mode;
        if(mode=="lesson"&&lessonId is not null)query+="&lessonId="+Uri.EscapeDataString(lessonId);
        if(group is not null)query+="&group="+Uri.EscapeDataString(group);
        if(subject is not null)query+="&subject="+subject;
        if(export&&studentId is not null)query+="&studentId="+Uri.EscapeDataString(studentId);
        return query;
    }
    public async Task Refresh(bool explicitRefresh=false)
    {
        if(disposed)return;
        if(loading){refreshPending=true;return;}
        if(selectedWork is not null&&!explicitRefresh)return;
        loading=true;var stamp=++generation;positions[Location]=scroll.Offset;
        try
        {
            if(!initialized){mode=(await api.Get<ResultsPreference>("teacher/results/preferences")).Mode;initialized=true;}
            lessons=await api.Get<LessonDashboard>("teacher/lessons"); if(older.Count==0)older.AddRange(lessons.History);
            var result=await api.Get<ResultsOverview>("teacher/results"+Query());
            if(disposed||stamp!=generation)return;data=result;
            if(studentId is not null&&data.Students.All(p=>p.Id!=studentId)){studentId=null;selectedWork=null;}
            if(selectedWork is { } previous){selectedWork=data.Works.SingleOrDefault(w=>w.StudentId==previous.StudentId&&w.Subject==previous.Subject&&w.TaskId==previous.TaskId&&w.LessonId==previous.LessonId);sqlDetail=null;submissions=null;}
            Render();if(selectedWork is not null)await LoadWork();
        }
        finally{loading=false;if(refreshPending&&!disposed){refreshPending=false;await Refresh(true);}}
    }
    void Navigate(Action change) { positions[Location]=scroll.Offset;generation++;change();Render(); }
    async Task ChangeFilter(Action change) { positions[Location]=scroll.Offset;generation++;change();studentId=null;selectedWork=null;scroll.Offset=positions.GetValueOrDefault(Location);await Refresh(); }
    static DataGrid Table(IEnumerable<Row> rows, params (string Caption,string Property)[] columns)
    {
        var table=new DataGrid{ItemsSource=rows.ToArray(),IsReadOnly=true,AutoGenerateColumns=false,MinHeight=150,MaxHeight=390,HorizontalAlignment=HorizontalAlignment.Stretch};
        foreach(var c in columns)table.Columns.Add(new DataGridTextColumn{Header=c.Caption,Binding=new Binding(c.Property),Width=c.Property=="Name"?new DataGridLength(1,DataGridLengthUnitType.Star):DataGridLength.Auto});
        return table;
    }
    void Render()
    {
        if(data is null||disposed)return;var stamp=generation;page.Children.Clear();
        var modes=new ComboBox{ItemsSource=new[]{"Занятие","Курс и выданные работы"},SelectedIndex=mode=="lesson"?0:1,MinWidth=190};
        modes.SelectionChanged+=async(_,_)=>await owner.Safe(()=>ChangeFilter(()=>{mode=modes.SelectedIndex==0?"lesson":"course";group=null;allStudents=false;}));
        var subjects=new ComboBox{ItemsSource=new[]{"Оба направления","SQL","Электроника"},SelectedIndex=subject=="sql"?1:subject=="electronics"?2:0,MinWidth=165};
        subjects.SelectionChanged+=async(_,_)=>await owner.Safe(()=>ChangeFilter(()=>subject=subjects.SelectedIndex==1?"sql":subjects.SelectedIndex==2?"electronics":null));
        var toolbar=Ui.Row(modes,subjects,Ui.Button("Обновить",()=>owner.Safe(()=>Refresh(true))));toolbar.Spacing=8;page.Children.Add(toolbar);
        if(mode=="lesson")
        {
            var choices=new List<Choice>{new(null,"Текущее занятие")};choices.AddRange(older.Select(l=>new Choice(l.Id,$"{Date(l.StartedAt??l.CreatedAt)} · {l.GroupName}")));
            if(lessonId is not null&&choices.All(c=>c.Id!=lessonId)&&lessons?.Current is { } current&&current.Id==lessonId)choices.Add(new(current.Id,current.GroupName));
            var select=new ComboBox{ItemsSource=choices,SelectedItem=choices.FirstOrDefault(c=>c.Id==lessonId)??choices[0],HorizontalAlignment=HorizontalAlignment.Stretch};
            select.SelectionChanged+=async(_,_)=>await owner.Safe(()=>ChangeFilter(()=>{lessonId=(select.SelectedItem as Choice)?.Id;group=null;allStudents=false;}));page.Children.Add(select);
            if(lessons?.HasMore==true)page.Children.Add(Ui.Button("Ещё занятия",()=>owner.Safe(async()=>{var more=await api.Get<LessonDashboard>("teacher/lessons?offset="+older.Count);older.AddRange(more.History.Where(l=>older.All(o=>o.Id!=l.Id)));lessons=more;Render();})));
        }
        var navigation=Ui.Row(Ui.Button("Все группы",()=>owner.Safe(async()=>{group=null;studentId=null;selectedWork=null;allStudents=false;await Refresh();})),Ui.Button("Все ученики",()=>NavigateAction(()=>{studentId=null;selectedWork=null;allStudents=true;})));
        if(studentId is not null)navigation.Children.Add(Ui.Button("К ученикам группы",()=>NavigateAction(()=>{studentId=null;selectedWork=null;allStudents=true;})));
        if(selectedWork is not null)navigation.Children.Add(Ui.Button("К работам ученика",()=>NavigateAction(()=>{selectedWork=null;sqlDetail=null;})));
        page.Children.Add(navigation);
        var pupil=data.Students.FirstOrDefault(p=>p.Id==studentId);page.Children.Add(Ui.Text(pupil?.Name??(group is null?data.Title:data.Groups.FirstOrDefault(g=>g.Key==group)?.Name??data.Title),24));
        if(selectedWork is not null){RenderWork(pupil!);RestorePosition(stamp);return;}
        var summary=pupil?.Summary??data.Summary;
        page.Children.Add(Ui.Card(Ui.Stack(Ui.Text($"Завершено работ: {summary.Completed} / {summary.Total} · Средний прогресс: {Percent(summary.AveragePercent)}",18),
            Ui.Text($"Учеников: {summary.Students}"+(mode=="lesson"?$" · Входили: {summary.Joined}":"")+$" · Начали работу: {summary.Started} · Ожидают проверки: {summary.Pending}"))));
        page.Children.Add(Ui.Text(data.Note,12));page.Children.Add(Ui.Text("Обновлено: "+Date(data.GeneratedAt)+" · даты по часам преподавателя",12));
        page.Children.Add(Ui.Row(Ui.Button("Сделать основным видом",()=>owner.Safe(async()=>{await api.Put<ResultsPreference>("teacher/results/preferences",new ResultsPreference(mode));owner.Message("Основной вид результатов сохранён.");})),Ui.Button("Экспорт этого вида в CSV",()=>owner.Safe(Export))));
        if(pupil is not null)RenderStudent(pupil);
        else if(!allStudents&&group is null)
        {
            var table=Table(data.Groups.Select(g=>new Row(g.Key,g.Name,$"{g.Summary.Completed}/{g.Summary.Total} · {Percent(g.Summary.AveragePercent)}",$"Учеников {g.Summary.Students}; начали {g.Summary.Started}","")),("Группа","Name"),("Завершение","Progress"),("Состав","State"));
            page.Children.Add(table);async Task Open(){if(table.SelectedItem is Row row){group=row.Id;allStudents=true;await Refresh();}}
            table.DoubleTapped+=async(_,_)=>await owner.Safe(Open);page.Children.Add(Ui.Button("Открыть группу",()=>owner.Safe(Open)));
        }
        else
        {
            var table=Table(data.Students.Select(p=>new Row(p.Id,p.Name,$"{p.Summary.Completed}/{p.Summary.Total} · {Percent(p.Summary.AveragePercent)}",p.Summary.Pending>0?$"На проверке: {p.Summary.Pending}":p.Summary.Started>0?"Работал":p.Joined?"Входил":"Не входил",Date(p.LastActivityAt))), ("Ученик","Name"),("Работы","Progress"),("Состояние","State"),("Последняя активность","Last"));
            page.Children.Add(table);void Open(){if(table.SelectedItem is Row row)Navigate(()=>studentId=row.Id);}
            table.DoubleTapped+=(_,_)=>Open();page.Children.Add(Ui.Button("Открыть ученика",()=>{Open();return Task.CompletedTask;}));
        }
        if(pupil is null)
        {
            page.Children.Add(Ui.Text("Задания, вызывающие трудности",20));
            page.Children.Add(Table(data.Difficulties.Select(d=>new Row(d.TaskId,(d.Subject=="sql"?"SQL · ":"Электроника · ")+d.Title,$"{d.Unsolved} / {d.Checked} · {Percent(d.UnsolvedPercent)}",d.SmallSample?"Мало данных":"",$"Выдано: {d.Assigned}")),("Задание","Name"),("Не завершили из проверенных","Progress"),("Выборка","State"),("Работ","Last")));
            page.Children.Add(Ui.Text("Знаменатель — работы, отправленные на SQL-проверку или уже проверенные преподавателем. Ожидание ручной проверки исключено. Нулевые ученики входят в средний прогресс; активная длительность показана отдельно в работе.",12));
        }
        RestorePosition(stamp);
    }
    Task NavigateAction(Action change){Navigate(change);return Task.CompletedTask;}
    void RestorePosition(int stamp){var key=Location;Dispatcher.UIThread.Post(()=>{if(!disposed&&generation==stamp&&Location==key)scroll.Offset=positions.GetValueOrDefault(key);},DispatcherPriority.Loaded);}
    void RenderStudent(ResultsStudent pupil)
    {
        var table=Table(data!.Works.Where(w=>w.StudentId==pupil.Id).Select(w=>new Row(w.Subject+":"+w.TaskId,w.Title+(w.Required?"":" · вне выдачи"),State(w),w.Grade is { }grade?grade.ToString():"—",Date(w.CompletedAt??w.LastActivityAt),w)),("Задание","Name"),("Состояние","Progress"),("Оценка","State"),("Дата","Last"));
        page.Children.Add(table);async Task Open(){if(table.SelectedItem is Row{Work:{ }work}){positions[Location]=scroll.Offset;selectedWork=work;sqlDetail=null;submissions=null;generation++;Render();await LoadWork();}}
        table.DoubleTapped+=async(_,_)=>await owner.Safe(Open);page.Children.Add(Ui.Button("Открыть работу",()=>owner.Safe(Open)));
    }
    async Task LoadWork(bool more=false)
    {
        if(selectedWork is {Subject:"electronics",AssignmentId:{ }assignment}scheme)
        {
            var version=generation;var result=await api.Get<ElectronicsReviewQueue>($"teacher/reviews/electronics?status=all&studentId={scheme.StudentId}&assignmentId={assignment}&offset="+(more?submissions?.NextOffset??0:0));
            if(disposed||version!=generation)return;positions[Location]=scroll.Offset;submissions=more&&submissions is not null?result with{Items=submissions.Items.Concat(result.Items).ToArray()}:result;Render();return;
        }
        if(selectedWork is not {Subject:"sql"}work)return;var stamp=generation;
        var route=$"teacher/results/sql/{work.StudentId}/{Uri.EscapeDataString(work.TaskId)}?offset="+(more?sqlDetail?.NextOffset??0:0)+(work.LessonId is { }id?"&lessonId="+id:"");
        var detail=await api.Get<ResultsSqlDetail>(route);if(disposed||stamp!=generation)return;
        positions[Location]=scroll.Offset;sqlDetail=more&&sqlDetail is not null?detail with{Events=sqlDetail.Events.Concat(detail.Events).ToArray()}:detail;Render();
    }
    void RenderWork(ResultsStudent pupil)
    {
        var work=selectedWork!;page.Children.Add(Ui.Text(work.Title,20));page.Children.Add(Ui.Text(State(work)+(work.Grade is { }grade?$" · Оценка: {grade}":"")));
        page.Children.Add(Ui.Text($"Открыто: {Date(work.StartedAt)} · Завершено: {Date(work.CompletedAt)}\nПроверок или сдач: {work.Checks} · Ошибок: {work.Errors} · Подсказок: {work.Hints?.ToString()??"не записывалось"} · Обнаруженных ограничений: {work.Violations?.ToString()??"не записывалось"}\nАктивная длительность: {(work.ActiveSeconds is { }seconds?$"{seconds} с":"не записывалась")}",13));
        page.Children.Add(Ui.Text("Ограничения — обнаруженные события, а не доказательство списывания.",12));
        if(work.Subject=="electronics")
        {
            if(work.ReportId is { } report)page.Children.Add(Ui.Primary(Ui.Button("Открыть сданную версию и проверку",()=>owner.Safe(async()=>{var view=new ElectronicsReviewQueueView(owner,api);owner.ShowPage("Сданная схема",view,view.Dispose);await view.OpenReport(pupil.Id,report);}))));
            else page.Children.Add(Ui.Text("Сданной версии пока нет. Работа может быть сохранена как черновик."));
            if(work.AssignmentId is not null)page.Children.Add(Ui.Button("Пошаговая история схемы",()=>owner.Safe(async()=>{var view=new ElectronicsJournalView(owner,api,pupil.Id,pupil.Name,work.AssignmentId,work.LessonId);owner.ShowPage("История схемы",view,view.Dispose);await view.Load();})));
            if(submissions is not null)
            {
                page.Children.Add(Ui.Text("История сдач",18));
                foreach(var submission in submissions.Items)page.Children.Add(Ui.Button($"{Date(submission.SubmittedAt)} · {ElectronicsReviewLabels.State(submission.State)}"+(submission.Grade is { }value?$" · оценка {value}":""),()=>owner.Safe(async()=>{var view=new ElectronicsReviewQueueView(owner,api);owner.ShowPage("Сданная схема",view,view.Dispose);await view.OpenReport(pupil.Id,submission.ReportId);})));
                if(submissions.HasMore)page.Children.Add(Ui.Button("Ещё сдачи",()=>owner.Safe(()=>LoadWork(true))));
            }
            return;
        }
        if(sqlDetail is null){page.Children.Add(Ui.Text("Загружается сохранённый ответ…"));return;}
        page.Children.Add(Ui.Text(sqlDetail.Prompt));
        page.Children.Add(Ui.Text(sqlDetail.Opened?$"Вариант {sqlDetail.Variant} · редакция {sqlDetail.Revision}"+(work.Reused?" · зачтено прежнее решение":""):"Задание ещё не открыто; персональный вариант пока не выдан.",12));
        var answer=Ui.Input(sqlDetail.AcceptedCode??sqlDetail.Draft,true);answer.IsReadOnly=true;answer.MaxHeight=230;page.Children.Add(Ui.Field(sqlDetail.AcceptedCode is null?"Черновик":"Принятый ответ",answer));
        page.Children.Add(Ui.Button("Пошаговая история SQL",()=>owner.Safe(async()=>{var view=new SqlJournalView(owner,api,pupil.Id,work.TaskId,work.Title,sqlDetail.WorkScope);owner.ShowPage("История SQL",view,view.Dispose);await view.Load();})));
        foreach(var attempt in sqlDetail.Events)
        {
            var text=$"{Date(attempt.At)} · {attempt.Action switch{"check"=>"Проверка","run"=>"Запуск","hint"=>"Подсказка",_=>attempt.Action}} · {attempt.Result switch{"correct"=>"Верно","different"=>"Результат отличается","executed"=>"Выполнено",_=>attempt.Result}}";
            var body=Ui.Stack();if(attempt.Code is { }code){var input=Ui.Input(code,true);input.IsReadOnly=true;input.MaxHeight=180;body.Children.Add(input);}
            if(attempt.Explanation is {Length:>0}explanation)body.Children.Add(Ui.Text(explanation));
            if(attempt.Action is "check" or "run"&&attempt.Code is not null)body.Children.Add(Ui.Button("Открыть точную проверочную среду",()=>owner.Safe(async()=>{var source=await api.Get<SqlReviewAttempt>("teacher/sql-review/attempts/"+Uri.EscapeDataString(attempt.Id));var view=new SqlAttemptReviewView(owner,api,source,true);owner.ShowPage("Сохранённая SQL-попытка",view,view.Dispose);} )));
            page.Children.Add(new Expander{Header=text,Content=body,HorizontalAlignment=HorizontalAlignment.Stretch});
        }
        if(sqlDetail.HasMore)page.Children.Add(Ui.Button("Ещё события",()=>owner.Safe(()=>LoadWork(true))));
    }
    async Task Export()
    {
        var csv=await api.Get<ResultsCsv>("teacher/results/csv"+Query(true));
        var file=await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions{Title="Экспорт результатов",SuggestedFileName="classroom-results.csv",FileTypeChoices=[new("CSV"){Patterns=["*.csv"]}]});
        if(file is null)return;await using var output=await file.OpenWriteAsync();if(output.CanSeek)output.SetLength(0);await using var writer=new StreamWriter(output,new UTF8Encoding(true));await writer.WriteAsync(csv.Csv);owner.Message("Выгрузка сформирована: "+Date(csv.GeneratedAt));
    }
    public void Dispose(){disposed=true;generation++;}
}
