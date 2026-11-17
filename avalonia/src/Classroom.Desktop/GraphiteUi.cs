using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Classroom.Desktop;

// Native, shared pieces for the approved design. They consume the current
// theme's resources, so choosing an older/custom theme continues to work.
static class GraphiteUi
{
    public static Border Panel(Control child)
    {
        var panel=new Border{Child=child};panel.Classes.Add("graphite-panel");return panel;
    }

    public static Button Action(string text,Func<Task> action,Func<bool> enabled,bool primary=false)
    {
        var button=Ui.Button(text,action,enabled);
        button.Classes.Add("graphite-action");
        button.MinHeight=34;button.Padding=new Thickness(10,7);
        if(primary)Ui.Primary(button);
        return button;
    }

    public static Border Separator()
    {
        var line=new Border{Height=1,Margin=new Thickness(0,4)};
        Ui.BindBrush(line,Border.BackgroundProperty,"Line");return line;
    }

    public static Grid WorkRow(string title,string detail,Button action)
    {
        var row=new Grid{ColumnDefinitions=new("*,Auto"),RowDefinitions=new("Auto,Auto"),ColumnSpacing=16,RowSpacing=6,Margin=new Thickness(0,12)};
        var name=Ui.Text(title,16);name.FontWeight=Avalonia.Media.FontWeight.SemiBold;
        row.Children.Add(name);
        var status=Ui.Muted(detail,14);Grid.SetRow(status,1);row.Children.Add(status);
        Grid.SetColumn(action,1);Grid.SetRowSpan(action,2);action.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(action);
        // At narrow widths and high DPI, leave the title/status readable and
        // move the command below them instead of squeezing text to a few letters.
        bool? compact=null;
        row.SizeChanged+=(_,_)=>
        {
            var narrow=row.Bounds.Width<480;
            if(compact==narrow)return;compact=narrow;
            row.ColumnDefinitions=new(narrow?"*":"*,Auto");row.RowDefinitions=new(narrow?"Auto,Auto,Auto":"Auto,Auto");
            Grid.SetColumn(action,narrow?0:1);Grid.SetRow(action,narrow?2:0);Grid.SetRowSpan(action,narrow?1:2);
            action.HorizontalAlignment=narrow?HorizontalAlignment.Left:HorizontalAlignment.Stretch;
        };
        return row;
    }
}
