using Avalonia.Controls;

namespace Classroom.Desktop.Electronics;

internal sealed class ElectronicsLocalCopiesView : UserControl,IDisposable
{
    readonly MainWindow owner;
    readonly Func<int,ElectronicsLocalPage> list;
    readonly Func<ElectronicsLocalEntry,ElectronicsLocalSnapshot> inspect;
    readonly Func<string,Task> clone;
    readonly Func<ElectronicsSelectionCandidate,Task> restore;
    readonly StackPanel rows=Ui.Stack(),details=Ui.Stack();
    readonly ContentControl preview=new();
    readonly Grid root;
    readonly string? currentLesson;
    Button? selectedButton;
    public bool IsDisposed{get;private set;}
    bool busy;
    int offset;
    public ElectronicsLocalCopiesView(MainWindow owner,Func<int,ElectronicsLocalPage> list,
        Func<ElectronicsLocalEntry,ElectronicsLocalSnapshot> inspect,Func<string,Task> clone,
        Func<ElectronicsSelectionCandidate,Task> restore,string? problem=null,string? currentLesson=null)
    {
        this.owner=owner;this.list=list;this.inspect=inspect;this.clone=clone;this.restore=restore;this.currentLesson=currentLesson;
        var tools=Ui.Stack(Ui.Text("Копии этого проекта со всех занятий на этом компьютере. Просмотр не отправляет схемы или результаты."),
            Ui.Button("Обновить список",()=>Act(()=>{Load(offset);return Task.CompletedTask;})));
        if(problem is not null)tools.Children.Insert(0,Ui.Text("Не удалось определить выбранную копию: файл выбора повреждён или недоступен. Исходные схемы и результаты сохранены. Просмотрите копии ниже."));
        var right=new Grid{RowDefinitions=new("Auto,*"),RowSpacing=12};right.Children.Add(Ui.Scroll(details));details.MaxHeight=400;Grid.SetRow(preview,1);right.Children.Add(preview);
        var split=new Grid{ColumnDefinitions=new("285,*"),ColumnSpacing=12};split.Children.Add(Ui.Scroll(rows));Grid.SetColumn(right,1);split.Children.Add(right);
        root=new Grid{RowDefinitions=new("Auto,*"),RowSpacing=12};root.Children.Add(tools);Grid.SetRow(split,1);root.Children.Add(split);Content=root;Load(0);
    }
    void Load(int at)
    {
        if(IsDisposed)return;var page=list(at);offset=at;rows.Children.Clear();details.Children.Clear();preview.Content=null;selectedButton=null;
        if(page.Entries.Length==0)rows.Children.Add(Ui.Muted("Локальных копий этого проекта нет."));
        foreach(var entry in page.Entries)
        {
            var label=(entry.Context.LocalCopyId is null?"Исходная копия":"Отдельная копия");
            Button? open=null;
            open=Ui.Button("Просмотреть копию",()=>Act(()=>{Show(inspect(entry));selectedButton?.Classes.Remove("primary");selectedButton=open;open!.Classes.Add("primary");return Task.CompletedTask;}));
            Avalonia.Automation.AutomationProperties.SetName(open,"Локальная копия "+entry.Context.LessonId+":"+(entry.Context.LocalCopyId??"original"));
            rows.Children.Add(Ui.Card(Ui.Stack(Ui.Text(label,16),Ui.Muted(entry.Context.LessonId==currentLesson?"Текущее занятие":"Другое занятие"),Ui.Muted("Файлы изменены: "+Ui.Time(entry.UpdatedAt)),open)));
        }
        var pages=Ui.Row();if(offset>0)pages.Children.Add(Ui.Button("Предыдущие",()=>Act(()=>{Load(Math.Max(0,offset-20));return Task.CompletedTask;})));
        if(page.HasMore)pages.Children.Add(Ui.Button("Следующие",()=>Act(()=>{Load(page.NextOffset);return Task.CompletedTask;})));
        rows.Children.Add(pages);
    }
    void Show(ElectronicsLocalSnapshot snapshot)
    {
        details.Children.Clear();preview.Content=null;
        details.Children.Add(Ui.Text(snapshot.Active?"Выбранная копия":"Сохранённая локальная копия",18));
        details.Children.Add(Ui.Muted((snapshot.Entry.Context.LocalCopyId is null?"Исходная копия":"Отдельная копия")+" · файлы изменены "+Ui.Time(snapshot.Entry.UpdatedAt)));
        if(snapshot.Problem is not null)details.Children.Add(Ui.Text(snapshot.Problem));
        if(snapshot.ProjectJson is not { }json)return;
        preview.Content=new ElectronicsSnapshotView(new(json,"draft",0,""),"Локальный снимок. Просмотр без редактирования и отправки.");
        details.Children.Add(Ui.Button("Сохранить отдельной схемой",()=>Act(()=>clone(json))));
        if(snapshot.Recovery is not { }candidate)return;
        details.Children.Add(Ui.Text($"Ожидают подтверждения: {candidate.Reports} результатов, из них сдач: {candidate.Submissions}. При выборе возобновится передача с прежними номерами и исходным занятием; ранее принятые результаты сервер проверит по этим номерам. Другие копии останутся на месте.",12));
        var confirm=new CheckBox{Content=Ui.Text("Возобновить передачу сохранённых результатов этой копии",12)};
        var choose=Ui.Button("Выбрать эту копию для продолжения",()=>Act(async()=>{if(confirm.IsChecked==true)await restore(candidate);}));choose.IsEnabled=false;
        confirm.IsCheckedChanged+=(_,_)=>choose.IsEnabled=confirm.IsChecked==true;
        details.Children.Add(confirm);details.Children.Add(choose);
    }
    async Task Act(Func<Task> action)
    {
        if(busy||IsDisposed)return;busy=true;root.IsEnabled=false;
        try{await owner.Safe(async()=>{if(!IsDisposed)await action();});}
        finally{busy=false;if(!IsDisposed)root.IsEnabled=true;}
    }
    public void Dispose(){IsDisposed=true;Content=null;}
}
