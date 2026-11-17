using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed class StudentHomeControl : UserControl
{
    readonly ClassroomApi api;
    readonly MainWindow owner;
    readonly Func<string, string?, Task> open;
    readonly StackPanel page = Ui.Stack();
    readonly List<Button> workActions = new();
    bool canWork,opening;
    string? previous;
    public StudentHomeControl(MainWindow owner, ClassroomApi api, Func<string, string?, Task> open)
    {
        this.owner = owner; this.api = api; this.open = open;
        page.Spacing=18;page.Margin=new Thickness(0,0,10,0);page.Children.Add(Ui.Text("Загружаются задания занятия…"));
        Content=Ui.Scroll(page);Margin=new Thickness(18,14);
    }
    Button Action(string label,string subject,string? id,bool primary=false)
    {
        var action=GraphiteUi.Action(label,async()=>
        {
            if(!canWork||opening)return;
            opening=true;EnableActions();
            try{await owner.Safe(()=>open(subject,id));}
            finally{opening=false;EnableActions();}
        },()=>canWork&&!opening,primary);
        action.IsEnabled=canWork&&!opening;workActions.Add(action);return action;
    }
    void EnableActions(){foreach(var action in workActions)action.IsEnabled=canWork&&!opening;}
    public async Task Refresh()
    {
        var data = await api.Get<StudentHomeDto>("student/home");
        var fingerprint = Wire.Write(data);
        if (fingerprint == previous) return;
        previous = fingerprint; page.Children.Clear();workActions.Clear();
        canWork = data.Lesson.Activated && data.Lesson.State == "running";
        page.Children.Add(Ui.Text("Моё занятие",26));
        var lessonState=!data.Lesson.Activated ? "Дождитесь начала или восстановления занятия преподавателем."
            : data.Lesson.State == "paused" ? "Занятие на паузе. Работы сохранены."
            : data.Lesson.State == "finished" ? "Занятие завершено. Работы сохранены." : "Занятие идёт";
        var subjects=(data.Lesson.AllowSql,data.Lesson.AllowElectronics) switch
        {(true,true)=>"SQL и электроника",(true,false)=>"Практика SQL",(false,true)=>"Электроника",_=>"Занятие"};
        var lesson=new Grid{ColumnDefinitions=new("*,Auto"),ColumnSpacing=18};
        lesson.Children.Add(Ui.Stack(Ui.Text(subjects,18),Ui.Muted(data.Lesson.GroupName,14)));
        var state=Ui.Text(lessonState,14);state.MaxWidth=340;state.VerticalAlignment=VerticalAlignment.Center;
        Grid.SetColumn(state,1);lesson.Children.Add(state);
        bool? lessonCompact=null;
        lesson.SizeChanged+=(_,_)=>
        {
            var compact=lesson.Bounds.Width<600;if(lessonCompact==compact)return;lessonCompact=compact;
            lesson.ColumnDefinitions=new(compact?"*":"*,Auto");lesson.RowDefinitions=new(compact?"Auto,Auto":"Auto");
            Grid.SetColumn(state,compact?0:1);Grid.SetRow(state,compact?1:0);lesson.RowSpacing=compact?12:0;
        };
        page.Children.Add(GraphiteUi.Panel(lesson));
        var rows=new StackPanel{Spacing=2};var count=0;
        void Add(string title,string detail,Button action)
        {
            if(count++>0)rows.Children.Add(GraphiteUi.Separator());
            rows.Children.Add(GraphiteUi.WorkRow(title,detail,action));
        }
        if (data.Lesson.AllowSql)
        {
            foreach (var work in data.Required)
            {
                var status = work.State switch { "completed-before" => "Решено раньше", "completed" => "Выполнено", "in-progress" => "В работе", _ => "Не начато" };
                if (work.RequireNewSolution && work.State == "not-started") status = "Нужно решить на занятии";
                var label=work.State switch{"completed-before" or "completed"=>"Посмотреть","in-progress"=>"Продолжить",_=>"Открыть"};
                Add(work.Title,"SQL · "+work.Module+" · "+status,Action(label,"sql",work.Id,work.State=="in-progress"));
            }
        }
        if (data.Lesson.AllowElectronics)
        {
            foreach (var work in data.Electronics ?? [])
            {
                var status = work.State switch { "awaiting-review" => "Ожидает проверки", "accepted" => "Принято", "changes-requested" => "На доработке", "rejected" => "Не принято", "draft" => "В работе", _ => "Не начато" };
                var label=work.State=="draft"?"Продолжить схему":"Открыть схему";
                Add(work.Title,"Электроника · "+status,Action(label,"electronics",work.TaskId,work.State=="draft"));
            }
        }
        var heading=new Grid{ColumnDefinitions=new("*,Auto"),ColumnSpacing=12};
        heading.Children.Add(Ui.Text("Выданные работы",16));
        var number=Ui.Muted($"Работ: {count}",12);Grid.SetColumn(number,1);heading.Children.Add(number);page.Children.Add(heading);
        if(count==0)rows.Children.Add(Ui.Muted("Преподаватель пока не выдал работы.",14));
        page.Children.Add(GraphiteUi.Panel(rows));
        // Preserve existing permissions: FreePractice gates SQL catalogue;
        // free electronic projects are available when electronics is allowed.
        if(data.Lesson.AllowSql&&data.Lesson.FreePractice||data.Lesson.AllowElectronics)
        {
            var practice=new StackPanel{Spacing=12};
            if(data.Lesson.AllowSql&&data.Lesson.FreePractice)
                practice.Children.Add(GraphiteUi.WorkRow("Свободная практика SQL","Задания из каталога",Action("Открыть каталог SQL","sql",null)));
            if(data.Lesson.AllowElectronics)
                practice.Children.Add(GraphiteUi.WorkRow("Мои схемы","Свободные проекты по электронике",Action("Открыть электронику","electronics",null)));
            page.Children.Add(GraphiteUi.Panel(practice));
        }
        page.Children.Add(Ui.Muted(data.Name+" · "+data.Group,12));
    }
}
