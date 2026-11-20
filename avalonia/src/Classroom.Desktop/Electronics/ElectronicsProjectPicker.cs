using Avalonia;
using Avalonia.Controls;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

internal sealed class ElectronicsProjectPicker : UserControl,IDisposable
{
    readonly MainWindow owner;
    readonly ClassroomApi api;
    readonly Func<string,Task> open;
    readonly StackPanel rows=Ui.Stack();
    readonly TextBox name=Ui.Input("Название схемы");
    readonly string? token;
    string? pendingId,pendingJson;
    bool disposed;
    public ElectronicsProjectPicker(MainWindow owner,ClassroomApi api,Func<string,Task> open,Func<Task> legacy)
    {
        this.owner=owner;this.api=api;this.open=open;token=api.Token;name.MaxLength=120;name.Name="FreeProjectName";
        var create=Ui.Primary(Ui.Button("Создать схему",()=>owner.Safe(Create)));create.Name="NewFreeProject";
        name.Width=360;
        var form=Ui.Row(name,create);form.MaxWidth=600;form.HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Left;
        Content=Ui.Scroll(Ui.Stack(Ui.Text("Мои схемы",28),Ui.Card(form),
            Ui.Row(Ui.Button("Обновить",()=>owner.Safe(()=>Refresh())),Ui.Button("Прежняя локальная схема",()=>owner.Safe(legacy))),rows));
        Margin=new Thickness(24);
    }
    bool Current=>!disposed&&api.Token==token;
    async Task Create()
    {
        if(!Current)return;
        if(pendingId is null)
        {
            var title=name.Text?.Trim()??"";
            if(title.Length==0||title.Any(char.IsControl))throw new InvalidDataException("Введите название схемы.");
            pendingId=Guid.NewGuid().ToString("N");pendingJson=new CircuitProject{Name=title}.Serialize();name.IsEnabled=false;
        }
        var created=await api.Put<ElectronicsProjectDto>("electronics/projects/"+pendingId,new ElectronicsProjectCreate(pendingJson!));
        if(!Current)return;
        pendingId=null;pendingJson=null;name.IsEnabled=true;name.Text="";await open(created.Id);
    }
    public async Task Refresh(int offset=0)
    {
        var data=await api.Get<ElectronicsProjectList>("electronics/projects?offset="+offset);
        if(!Current)return;rows.Children.Clear();
        if(data.Projects.Length==0)rows.Children.Add(Ui.Muted("Сохранённых схем пока нет."));
        foreach(var p in data.Projects)
        {
            var title=string.IsNullOrWhiteSpace(p.Name)?"Без названия":p.Name;
            var button=Ui.Button("Открыть",()=>owner.Safe(async()=>{if(Current)await open(p.Id);}));
            var copies=Ui.Button("Копии на этом ПК",()=>owner.Safe(async()=>{if(Current)await owner.ShowFreeLocalCopies(p.Id);}));
            Avalonia.Automation.AutomationProperties.SetName(copies,"Копии «"+title+"»");
            Avalonia.Automation.AutomationProperties.SetName(button,"Открыть «"+title+"»");
            var description=Ui.Stack(Ui.Text(title,18),Ui.Muted(ElectronicsReviewLabels.State(p.State)+" · "+ElectronicsLabels.Date(p.UpdatedAt)));description.Spacing=2;
            var row=new Grid{ColumnDefinitions=new("*,Auto"),ColumnSpacing=18};row.Children.Add(description);var tools=Ui.Row(button,copies);Grid.SetColumn(tools,1);row.Children.Add(tools);
            button.VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center;
            var card=Ui.Card(row);card.Padding=new Thickness(18,12);rows.Children.Add(card);
        }
        var pages=Ui.Row();
        if(offset>0)pages.Children.Add(Ui.Button("Назад",()=>owner.Safe(()=>Refresh(Math.Max(0,offset-30)))));
        if(data.HasMore)pages.Children.Add(Ui.Button("Далее",()=>owner.Safe(()=>Refresh(data.NextOffset))));
        if(pages.Children.Count>0)rows.Children.Add(pages);
    }
    public void Dispose()=>disposed=true;
}
