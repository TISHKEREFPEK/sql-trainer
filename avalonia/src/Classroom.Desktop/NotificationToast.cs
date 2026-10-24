using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Classroom.Desktop;

public sealed class NotificationToast : Border
{
    readonly TextBlock heading = new() { FontSize=16,FontWeight=FontWeight.SemiBold,Foreground=Brushes.White };
    readonly TextBlock message = new() { FontSize=14,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.White };
    readonly DispatcherTimer timer = new() { Interval=TimeSpan.FromMilliseconds(2500) };
    string current="";
    bool currentError;
    public NotificationToast()
    {
        IsVisible=false; HorizontalAlignment=HorizontalAlignment.Right; VerticalAlignment=VerticalAlignment.Top;
        Margin=new Thickness(18,12,18,0); Padding=new Thickness(16); CornerRadius=new CornerRadius(10);
        BorderBrush=Brushes.White; BorderThickness=new Thickness(1); MinWidth=280; MaxWidth=420;
        var close=new Button { Content="×",FontSize=20,Foreground=Brushes.White,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(5,0),MinHeight=24 };
        AutomationProperties.SetName(close,"Закрыть уведомление"); close.Click+=(_,_)=>Dismiss();
        var header=new Grid { ColumnDefinitions=new("*,Auto") }; header.Children.Add(heading); Grid.SetColumn(close,1); header.Children.Add(close);
        Child=new StackPanel { Spacing=8,Children={header,message} };
        timer.Tick+=(_,_)=>Dismiss();
    }
    public void ShowMessage(string text,bool error)
    {
        if(string.IsNullOrWhiteSpace(text)) { Dismiss(); return; }
        if(IsVisible && current==text && currentError==error) return;
        timer.Stop(); current=text; currentError=error;
        heading.Text=error?"Ошибка":"Уведомление";
        message.Text=text.Length>400?text[..397]+"…":text;
        Background=Brush.Parse(error?"#B42318":"#175CD3");
        AutomationProperties.SetName(this,heading.Text+": "+message.Text);
        IsVisible=true; timer.Start();
    }
    public void Dismiss()
    {
        timer.Stop(); IsVisible=false; current=""; message.Text="";
        AutomationProperties.SetName(this,"Уведомление");
    }
}
