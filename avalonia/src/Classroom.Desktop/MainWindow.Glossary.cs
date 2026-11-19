using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Classroom.Desktop;

public sealed partial class MainWindow
{
    Grid? glossaryOverlay;
    internal bool IsGlossaryOpen => glossaryOverlay is not null;

    bool HandleGlossaryKey(KeyEventArgs e)
    {
        if (glossaryOverlay is not { } overlay) return false;
        if (e.Key == Key.Escape) CloseGlossary();
        else if (e.Key == Key.Enter && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) return false;
        else if (e.Source is Visual source && (ReferenceEquals(source, overlay) || overlay.IsVisualAncestorOf(source))) return false;
        e.Handled = true;
        return true;
    }

    internal async Task ShowGlossaryDialog(IReadOnlyDictionary<string,string> glossary, Control? trigger)
    {
        if (IsGlossaryOpen || stopping || quitting) return;
        var returnFocus = trigger ?? FocusManager?.GetFocusedElement() as Control;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var filter = Ui.Input();
        filter.Watermark = "Поиск термина";
        AutomationProperties.SetName(filter, "Поиск термина");
        var list = Ui.Stack();
        void Update()
        {
            list.Children.Clear();
            foreach (var entry in glossary.Where(g => g.Key.Contains(filter.Text ?? "", StringComparison.OrdinalIgnoreCase)))
                list.Children.Add(new Expander { Header = entry.Key, Content = Ui.Text(entry.Value) });
            if (list.Children.Count == 0) list.Children.Add(Ui.Muted("По этому запросу ничего не найдено"));
        }
        filter.TextChanged += (_, _) => Update();
        Update();

        var close = Ui.Button("Закрыть", () => { CloseGlossary(); return Task.CompletedTask; });
        var title = Ui.Text("Справочник SQL", 20);
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(title);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var content = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 12 };
        content.Children.Add(header);
        Grid.SetRow(filter, 1);
        content.Children.Add(filter);
        var scroll = Ui.Scroll(list);
        Grid.SetRow(scroll, 2);
        content.Children.Add(scroll);

        var panel = GraphiteUi.Panel(content);
        panel.Width = Math.Min(460, Math.Max(200, ClientSize.Width - 64));
        panel.MaxWidth = 460;
        panel.MaxHeight = 540;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Stretch;
        panel.Margin = new Thickness(16);
        AutomationProperties.SetName(panel, "Справочник SQL");
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(65, 35, 35, 39)) };
        Grid.SetColumnSpan(overlay, 2);
        Grid.SetRowSpan(overlay, 3);
        overlay.Children.Add(panel);
        overlay.DetachedFromVisualTree += (_, _) => closed.TrySetResult();
        glossaryOverlay = overlay;
        shellRoot.Children.Add(overlay);
        Dispatcher.UIThread.Post(() => filter.Focus(), DispatcherPriority.Loaded);
        try { await closed.Task; }
        finally
        {
            if (ReferenceEquals(glossaryOverlay, overlay)) glossaryOverlay = null;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsGlossaryOpen && returnFocus?.IsEffectivelyVisible == true && returnFocus.IsEffectivelyEnabled)
                    returnFocus.Focus();
            }, DispatcherPriority.Background);
        }
    }

    void CloseGlossary()
    {
        if (glossaryOverlay is not { } overlay) return;
        glossaryOverlay = null;
        shellRoot.Children.Remove(overlay);
    }
}
