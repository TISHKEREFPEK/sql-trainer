using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Classroom.Desktop;

public sealed partial class MainWindow
{
    readonly Grid shellRoot;
    readonly Border shellHeader;
    StudentNavigation? studentNavigation;

    void StartStudentNavigation()
    {
        if(teacher||login is null||api is null||studentNavigation is not null)return;
        var navigation=new StudentNavigation(key=>Safe(async()=>
        {
            if(stopping||quitting)return;
            switch(key)
            {
                case "home":await ShowStudentHome();break;
                case "sql":await SwitchStudentSubject("sql");break;
                case "electronics":await SwitchStudentSubject("electronics");break;
                case "glossary":
                    student??=new(this,api);await student.OpenGlossary();break;
                case "review":await ShowTeacherReview();break;
            }
        }),ConfigureStudentPopup);
        studentNavigation=navigation;
        sqlSubjectButton=navigation.Entry("sql");electronicsSubjectButton=navigation.Entry("electronics");
        if(actions.Parent is Panel old)old.Children.Remove(actions);
        actions.MaxWidth=144;actions.ItemSpacing=4;actions.LineSpacing=6;actions.HorizontalAlignment=HorizontalAlignment.Stretch;
        foreach(var button in actions.Children.OfType<Button>()){button.FontSize=12;button.Padding=new Thickness(4,6);button.MinHeight=32;}
        navigation.SetUtilities(actions);
        foreach(var child in shellRoot.Children)Grid.SetColumn(child,1);
        shellRoot.ColumnDefinitions[0].Width=new GridLength(168);shellRoot.ColumnSpacing=18;shellRoot.Margin=new Thickness(14);
        shellHeader.IsVisible=false;Grid.SetRowSpan(navigation,3);shellRoot.Children.Insert(0,navigation);
        MinWidth=640;MinHeight=360;
        navigation.BusyChanged+=busy=>
        {
            if(busy)body.IsEnabled=false;
            else _=Safe(RefreshLessonPermission);
        };
        UpdateStudentNavigation();
    }

    void StopStudentNavigation()
    {
        if(studentNavigation is null)return;
        studentNavigation.SetWork(null,null);studentNavigation.SetUtilities(null);
        student?.RestoreNavigation();shellRoot.Children.Remove(studentNavigation);studentNavigation=null;
        shellRoot.ColumnDefinitions[0].Width=new GridLength(0);shellRoot.ColumnSpacing=0;shellRoot.Margin=new Thickness(0);
        shellHeader.IsVisible=true;
        if(actions.Parent is Panel old)old.Children.Remove(actions);
        actions.MaxWidth=680;actions.ItemSpacing=10;actions.LineSpacing=8;actions.HorizontalAlignment=HorizontalAlignment.Right;
        Grid.SetColumn(actions,1);((Grid)shellHeader.Child!).Children.Add(actions);
        MinWidth=850;MinHeight=600;
    }

    void UpdateStudentNavigation()
    {
        if(studentNavigation is null)return;
        var current=pageOpen?pageReturnContent:body.Content;
        var route=teacherReview is not null?"review":current is StudentScreen?"sql":current is StudentHomeControl?"home":"electronics";
        studentNavigation.Select(route);studentNavigation.SetOverlay(pageOpen);
        if(current is StudentScreen sql)
        {
            var nav=sql.TakeNavigation();studentNavigation.SetWork(nav.Body,nav.Summary);
        }
        else studentNavigation.SetWork(null,null);
        shellRoot.ColumnDefinitions[0].Width=new GridLength(Bounds.Width<850?52:168);
        shellRoot.ColumnSpacing=Bounds.Width<850?13:18;
    }
    void ConfigureStudentPopup(Control content)
    {
        content.AddHandler(KeyDownEvent,OnKey,Avalonia.Interactivity.RoutingStrategies.Tunnel);
        content.AddHandler(Avalonia.Input.DragDrop.DropEvent,OnDrop,Avalonia.Interactivity.RoutingStrategies.Tunnel);
        content.AddHandler(Avalonia.Input.DragDrop.DragOverEvent,OnDrop,Avalonia.Interactivity.RoutingStrategies.Tunnel);
        content.AddHandler(TextBox.CopyingToClipboardEvent,OnClipboard);
        content.AddHandler(TextBox.CuttingToClipboardEvent,OnClipboard);
        content.AddHandler(TextBox.PastingFromClipboardEvent,OnClipboard);
    }
}
