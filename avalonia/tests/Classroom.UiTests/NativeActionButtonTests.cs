using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Classroom.Desktop;
using Xunit;

namespace Classroom.UiTests;

public sealed class NativeActionButtonTests
{
    [AvaloniaFact]
    public async Task RepeatedPointerKeyboardAndRoutedCommandsDoNotRepeatPendingAction()
    {
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count=0;var allowed=true;
        var button=Ui.Primary(Ui.Button("Проверить",async()=>{count++;await release.Task;},()=>allowed));
        var window=new Window{Content=button,Width=340,Height=180};window.Show();window.UpdateLayout();
        try
        {
            button.Focus(NavigationMethod.Tab);Assert.True(button.IsFocused);
            var at=button.TranslatePoint(new Point(button.Bounds.Width/2,button.Bounds.Height/2),window)!.Value;
            window.MouseMove(at);window.MouseDown(at,MouseButton.Left);window.MouseUp(at,MouseButton.Left);
            Assert.Equal(1,count);Assert.False(button.IsEnabled);
            window.MouseDown(at,MouseButton.Left);window.MouseUp(at,MouseButton.Left);
            window.KeyPressQwerty(PhysicalKey.Space,RawInputModifiers.None);window.KeyReleaseQwerty(PhysicalKey.Space,RawInputModifiers.None);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1,count);
            // Permission can change while a command is awaiting its result.
            allowed=false;release.SetResult();
            for(var i=0;i<100&&button.Classes.Contains("busy");i++)await Task.Delay(10);
            Assert.DoesNotContain("busy",button.Classes);Assert.False(button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(1,count);
        }
        finally{release.TrySetResult();window.Close();}
    }

    [AvaloniaFact]
    public void DisabledParentCannotBeBypassedByRoutedCommand()
    {
        var count=0;var button=Ui.Button("Открыть",()=>{count++;return Task.CompletedTask;});
        var parent=new Border{Child=button,IsEnabled=false};var window=new Window{Content=parent};window.Show();
        try
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(0,count);
            parent.IsEnabled=true;button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(1,count);
            Assert.True(button.IsEnabled);
        }
        finally{window.Close();}
    }
}
