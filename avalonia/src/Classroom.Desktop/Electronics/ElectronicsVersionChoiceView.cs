using Avalonia.Controls;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

// Every preview is read-only. Only the explicit server-choice callback switches
// the durable active pointer; closing this page keeps the original editor.
internal sealed class ElectronicsVersionChoiceView : UserControl,IDisposable
{
    readonly MainWindow owner;
    readonly ContentControl serverPreview=new(),archivePreview=new();
    readonly TextBlock message=Ui.Text("",13);
    readonly StackPanel archiveRows=Ui.Stack();
    readonly Func<ElectronicsProjectDto,Task> selectServer;
    readonly Func<string,Task> copy;
    readonly string localJson;
    readonly Grid root;
    ElectronicsProjectDto server;
    public bool IsDisposed{get;private set;}
    bool busy;
    public ElectronicsVersionChoiceView(MainWindow owner,string localJson,ElectronicsProjectDto server,
        Func<ElectronicsProjectDto,Task> selectServer,Func<string,Task> copy,Action keepLocal,
        ElectronicsLocalCopy[] archives,Func<Task>? allCopies=null)
    {
        this.owner=owner;this.localJson=localJson;this.server=server;this.selectServer=selectServer;this.copy=copy;
        var actions=Ui.Stack(Ui.Row(Ui.Button("Продолжить локальную",()=>Act(()=>{keepLocal();return Task.CompletedTask;})),
            Ui.Button("Сохранить локальную отдельно",()=>Act(()=>copy(localJson)))),
            Ui.Button("Продолжить с серверной версией",()=>Act(()=>selectServer(this.server))));
        var warning=Ui.Muted("При выборе серверной версии нынешняя локальная копия, история и неподтверждённые сдачи останутся в «Локальных копиях». Их автоматическая отправка остановится. Их можно просмотреть и сохранить отдельной схемой.");
        var tabs=new TabControl{ItemsSource=new[]{
            new TabItem{Header="Локальная версия",Content=new ElectronicsSnapshotView(new(localJson,"draft",0,""),"Сохранённая версия на этом компьютере. Просмотр без редактирования.")},
            new TabItem{Header="Серверная версия",Content=serverPreview},
            new TabItem{Header="Локальные копии",Content=ArchivePanel(archives,allCopies)}}};
        root=new Grid{RowDefinitions=new("Auto,Auto,Auto,*"),RowSpacing=10};
        root.Children.Add(actions);Grid.SetRow(warning,1);root.Children.Add(warning);Grid.SetRow(message,2);root.Children.Add(message);Grid.SetRow(tabs,3);root.Children.Add(tabs);
        Content=root;UpdateServer(server);
    }
    Control ArchivePanel(ElectronicsLocalCopy[] archives,Func<Task>? allCopies)
    {
        if(allCopies is not null)archiveRows.Children.Add(Ui.Button("Копии со всех занятий",()=>Act(allCopies)));
        if(archives.Length==0)archiveRows.Children.Add(Ui.Muted("Других локальных копий этого проекта в занятии нет."));
        foreach(var item in archives)
        {
            if(item.Problem is not null)archiveRows.Children.Add(Ui.Card(Ui.Text(item.Problem)));
            if(item.ProjectJson.Length==0)continue;
            var label=(item.Context.LocalCopyId is null?"Исходная копия":"Отдельная копия")+" · "+Ui.Time(item.UpdatedAt);
            archiveRows.Children.Add(Ui.Card(Ui.Stack(Ui.Text(item.Title,16),Ui.Muted(label),
                Ui.Stack(Ui.Button("Просмотреть",()=>Act(()=>{archivePreview.Content=new ElectronicsSnapshotView(new(item.ProjectJson,"draft",0,""),"Сохранённая локальная копия. Она не отправляется автоматически.");return Task.CompletedTask;})),
                Ui.Button("Сохранить отдельной схемой",()=>Act(()=>copy(item.ProjectJson)))))));
        }
        var panel=new Grid{ColumnDefinitions=new("310,*"),ColumnSpacing=12};var list=Ui.Scroll(archiveRows);panel.Children.Add(list);Grid.SetColumn(archivePreview,1);panel.Children.Add(archivePreview);return panel;
    }
    public void UpdateServer(ElectronicsProjectDto value)
    {
        if(IsDisposed)return;server=value;
        serverPreview.Content=new ElectronicsSnapshotView(new(value.ProjectJson,"draft",value.UpdatedAt,""),"Серверная версия · сохранено "+Ui.Time(value.UpdatedAt)+". Просмотр без редактирования.");
        message.Text=value.ProjectJson==localJson?"Содержимое версий совпадает.":"Содержимое локальной и серверной версий различается. Выберите, с какой продолжить.";
    }
    async Task Act(Func<Task> action)
    {
        if(IsDisposed||busy)return;busy=true;root.IsEnabled=false;
        try{await owner.Safe(async()=>{if(!IsDisposed)await action();});}
        finally{busy=false;if(!IsDisposed)root.IsEnabled=true;}
    }
    public void Dispose(){IsDisposed=true;Content=null;}
}
