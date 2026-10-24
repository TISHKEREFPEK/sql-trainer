using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Classroom.Client;
using Classroom.Desktop;
using Xunit;

namespace Classroom.UiTests;
public sealed class NotificationTests
{
    [AvaloniaFact]
    public async Task WrongPasswordShowsProminentToastAndDisappearsAutomatically()
    {
        await using var fixture=await LiveClassroom.Start();
        var window=new MainWindow(true,false){Width=900,Height=900};
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(MainWindow).GetField("api",flags)!.SetValue(window,fixture.Admin);
        await (Task)typeof(MainWindow).GetMethod("ShowLogin",flags)!.Invoke(window,null)!; window.Show(); window.UpdateLayout();
        window.GetVisualDescendants().OfType<TextBox>().Single().Text="wrong-password";
        window.GetVisualDescendants().OfType<Button>().Single(b=>b.Content?.ToString()=="Войти").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var toast=window.GetVisualDescendants().OfType<NotificationToast>().Single();
        for(var i=0;i<100&&!toast.IsVisible;i++) await Task.Delay(50);
        Assert.True(toast.IsVisible); Assert.Contains(toast.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="Неверный логин или пароль.");
        window.UpdateLayout(); Assert.True(toast.Bounds.Width>=280); Assert.True(toast.Bounds.Right<=window.Bounds.Width);
        var output=Environment.GetEnvironmentVariable("CLASSROOM_UI_OUTPUT")??Path.Combine(AppContext.BaseDirectory,"screenshots"); Directory.CreateDirectory(output);
        foreach(var width in new[]{900,1320}) { window.Width=width; window.UpdateLayout(); using var frame=window.CaptureRenderedFrame(); frame!.Save(Path.Combine(output,$"notification-{width}.png")); }
        await Task.Delay(2900); Assert.False(toast.IsVisible);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="Неверный логин или пароль."); window.Close();
    }
    [AvaloniaFact]
    public async Task NewMessageRestartsTimeoutAndCloseButtonDismissesIt()
    {
        var window=new MainWindow(false,false); window.Show();
        var toast=window.GetVisualDescendants().OfType<NotificationToast>().Single();
        await window.Safe(()=>throw new IOException("Первая ошибка")); Assert.True(toast.IsVisible);
        await Task.Delay(1500); window.Error("Вторая ошибка"); await Task.Delay(1300);
        Assert.True(toast.IsVisible); Assert.Contains(toast.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text=="Вторая ошибка");
        await Task.Delay(1500); Assert.False(toast.IsVisible);
        window.Message("Сохранено."); Assert.True(toast.IsVisible);
        toast.GetVisualDescendants().OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.False(toast.IsVisible); window.Close();
    }
}
