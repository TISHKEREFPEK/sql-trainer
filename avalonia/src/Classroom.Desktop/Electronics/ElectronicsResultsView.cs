using Avalonia.Controls;
using Classroom.Client;
using Classroom.Contracts;
namespace Classroom.Desktop.Electronics;

public sealed class ElectronicsResultsView : UserControl,IDisposable
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    int generation;
    bool disposed;
    public ElectronicsResultsView(MainWindow owner,ClassroomApi api){this.owner=owner;this.api=api;Content=Ui.Text("Откройте вкладку, чтобы загрузить результаты.");}
    public async Task Load()
    {
        var request=++generation; var data=await api.Get<ElectronicsOverview>("teacher/electronics"); if(disposed||request!=generation)return;
        var filter=Ui.Input();filter.Watermark="Поиск ученика";filter.Width=230;
        var group=new ComboBox{MinWidth=180,ItemsSource=new[]{new Choice("*","Все группы"),new Choice("","Без группы")}.Concat(data.Groups.Select(g=>new Choice(g.Id,g.Name))).ToArray(),SelectedIndex=0};
        var list=new ListBox(); var empty=Ui.Text("");
        void Update(){var selected=(group.SelectedItem as Choice)?.Id??"*";var rows=data.Students.Where(s=>(selected=="*"||s.GroupId== (selected==""?null:selected))&&s.Login.Contains(filter.Text??"",StringComparison.OrdinalIgnoreCase)).ToArray();list.ItemsSource=rows.Select(s=>new Choice(s.StudentId,$"{s.Login} · {data.Groups.FirstOrDefault(g=>g.Id==s.GroupId)?.Name??"Без группы"} — {ElectronicsLabels.Kind(s.LastKind)}\nОтправок: {s.Submitted}; компиляций: {s.Compiled}; ошибок: {s.Errors}"+(s.LastAt is {} at?" · "+ElectronicsLabels.Date(at):""))).ToArray();empty.Text=rows.Length==0?"Ученики не найдены.":"";}
        group.SelectionChanged+=(_,_)=>Update();filter.TextChanged+=(_,_)=>Update();
        list.SelectionChanged+=async(_,_)=>{if(list.SelectedItem is Choice choice) await owner.Safe(()=>Student(choice.Id,0));};
        var root=new Grid{RowDefinitions=new("Auto,Auto,Auto,*"),RowSpacing=10,Margin=new Avalonia.Thickness(8)};
        root.Children.Add(Ui.Row(Ui.Text("Результаты учеников · Электроника",22),Ui.Button("Обновить",()=>owner.Safe(Load))));
        var search=Ui.Stack(Ui.Text("Схемы и код сохраняются отдельно от SQL. Успешная компиляция не означает правильное выполнение задания.",12),Ui.Row(group,filter));Grid.SetRow(search,1);root.Children.Add(search);Grid.SetRow(empty,2);root.Children.Add(empty);Grid.SetRow(list,3);root.Children.Add(list);Content=root;Update();
    }
    async Task Student(string id,int offset)
    {
        var request=++generation;var data=await api.Get<ElectronicsStudentDetail>($"teacher/electronics/{Uri.EscapeDataString(id)}?offset={offset}");if(disposed||request!=generation)return;
        var preview=new ContentControl{Content=Ui.Text("Выберите сохранённую схему или событие.")};
        var works=new ComboBox{HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Stretch,ItemsSource=data.Works.Select((w,i)=>new WorkChoice(w,$"Копия {i+1}: {w.Name} · {w.Parts} деталей, {w.Wires} проводов")).ToArray()};
        var reports=new ListBox{ItemsSource=data.Reports.Select(r=>new Choice(r.Id,ElectronicsLabels.Kind(r.Kind)+"\n"+ElectronicsLabels.Date(r.At)+" · "+r.Name)).ToArray()};
        async Task Snapshot(string route)
        {
            var version=++generation;var snapshot=await api.Get<ElectronicsSnapshot>(route);if(disposed||version!=generation)return;preview.Content=new ElectronicsSnapshotView(snapshot);
        }
        works.SelectionChanged+=async(_,_)=>{if(works.SelectedItem is WorkChoice work){reports.SelectedItem=null;await owner.Safe(()=>Snapshot($"teacher/electronics/{Uri.EscapeDataString(id)}/work/{work.Work.ClientId}"));}};
        reports.SelectionChanged+=async(_,_)=>{if(reports.SelectedItem is Choice report){works.SelectedItem=null;await owner.Safe(()=>Snapshot($"teacher/electronics/{Uri.EscapeDataString(id)}/report/{report.Id}"));}};
        var left=new Grid{RowDefinitions=new("Auto,Auto,Auto,*,Auto"),RowSpacing=8};left.Children.Add(Ui.Text("Текущие схемы",16));Grid.SetRow(works,1);left.Children.Add(works);var title=Ui.Text("История запусков и отправок",16);Grid.SetRow(title,2);left.Children.Add(title);Grid.SetRow(reports,3);left.Children.Add(reports);
        var navigation=Ui.Row(Ui.Button("Раньше",()=>owner.Safe(()=>Student(id,data.NextOffset))),Ui.Button("Позже",()=>owner.Safe(()=>Student(id,Math.Max(0,offset-30)))));navigation.Children[0].IsEnabled=data.HasMore;navigation.Children[1].IsEnabled=offset>0;Grid.SetRow(navigation,4);left.Children.Add(navigation);
        var body=new Grid{ColumnDefinitions=new("260,*"),ColumnSpacing=12};body.Children.Add(left);Grid.SetColumn(preview,1);body.Children.Add(preview);
        var root=new Grid{RowDefinitions=new("Auto,*"),RowSpacing=10,Margin=new Avalonia.Thickness(8)};root.Children.Add(Ui.Row(Ui.Button("Назад к списку",()=>owner.Safe(Load)),Ui.Text(data.Login,22),Ui.Button("Обновить",()=>owner.Safe(()=>Student(id,offset)))));Grid.SetRow(body,1);root.Children.Add(body);Content=root;
        if(data.Works.Length>0)works.SelectedIndex=0;else preview.Content=Ui.Text("Ученик ещё не передавал работу. Локальные файлы появятся здесь после подключения его приложения.");
    }
    public void Dispose(){disposed=true;generation++;}
    sealed record Choice(string Id,string Label){public override string ToString()=>Label;}
    sealed record WorkChoice(ElectronicsWorkInfo Work,string Label){public override string ToString()=>Label;}
}

