using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    Border? partsPanel, inspectorPanel;
    Grid? splitWorkspace;
    TabControl? codeTabs;
    GridSplitter? codeSplitter;
    Button? partsToggle, inspectorToggle, codeToggle;
    bool partsShown = true, inspectorShown, codeShown = true;
    string? inspectedSelection;
    GridLength sceneHeight = new(3, GridUnitType.Star), codeHeight = new(2, GridUnitType.Star);

    Control BuildWorkspace(Grid scene, Border parts, Border inspector, Control code)
    {
        partsPanel = parts; inspectorPanel = inspector;
        partsToggle = PanelButton("Панель деталей", () =>
        {
            partsShown = !partsShown;
            if (partsShown && scene.Bounds.Width < 1100) inspectorShown = false;
            UpdateWorkspacePanels();
        });
        inspectorToggle = PanelButton("Панель свойств", () =>
        {
            inspectorShown = !inspectorShown;
            if (inspectorShown && scene.Bounds.Width < 1100) partsShown = false;
            if (inspectorShown && selected is null && selectedWire is null)
            {
                properties.Children.Clear(); properties.Children.Add(Ui.Text("Выберите деталь или провод на схеме."));
            }
            UpdateWorkspacePanels();
        });
        codeToggle = PanelButton("Панель кода", () =>
        {
            if (codeShown)
            {
                sceneHeight = splitWorkspace!.RowDefinitions[1].Height;
                codeHeight = splitWorkspace.RowDefinitions[3].Height;
            }
            codeShown = !codeShown; UpdateWorkspacePanels();
        });
        var panels = Ui.Row(partsToggle, inspectorToggle, codeToggle); panels.Spacing = 6;
        var split = splitWorkspace = new Grid { Name = "ElectronicsSplitWorkspace", RowDefinitions = new("Auto,3*,6,2*"), RowSpacing = 0 };
        split.Children.Add(panels); Grid.SetRow(scene, 1); split.Children.Add(scene);
        split.RowDefinitions[1].MinHeight = 160;
        codeSplitter = new GridSplitter { Name = "ElectronicsCodeSplitter", ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(codeSplitter, "Размер схемы и кода");
        ToolTip.SetTip(codeSplitter, "Перетащите границу, чтобы изменить размер схемы и кода.");
        Ui.BindBrush(codeSplitter, GridSplitter.BackgroundProperty, "Line");
        Grid.SetRow(codeSplitter, 2); split.Children.Add(codeSplitter);
        codeTabs = new TabControl { Name = "ElectronicsCodeTabs", ItemsSource = new[]
        {
            new TabItem { Header = "Код Arduino Uno", Content = code },
            new TabItem { Header = "Последовательный монитор", Content = monitor }
        }};
        split.RowDefinitions[3].MinHeight = 220;
        Grid.SetRow(codeTabs, 3); split.Children.Add(codeTabs);
        scene.SizeChanged += (_, _) =>
        {
            if (scene.Bounds.Width is > 0 and < 1100 && partsShown && inspectorShown)
            { partsShown = false; UpdateWorkspacePanels(); }
        };
        UpdateWorkspacePanels();
        return split;
    }

    static Button PanelButton(string accessibleName, Action action)
    {
        var button = Ui.Button("", () => { action(); return Task.CompletedTask; });
        button.FontSize = 12; button.Padding = new Thickness(10, 6); button.MinHeight = 32;
        button.Margin = new Thickness(0, 0, 0, 6); AutomationProperties.SetName(button, accessibleName); return button;
    }

    void InspectPanelSelection()
    {
        var key = selected is not null ? "part:" + selected : selectedWire is not null ? "wire:" + selectedWire : null;
        if (key != inspectedSelection)
        {
            inspectedSelection = key; inspectorShown = key is not null;
            if (inspectorShown && workGrid.Bounds.Width is > 0 and < 1100) partsShown = false;
        }
        UpdateWorkspacePanels();
        if (inspectorShown && key is null) properties.Children.Add(Ui.Text("Выберите деталь или провод на схеме."));
    }

    void UpdateWorkspacePanels()
    {
        if (splitWorkspace is null) return;
        partsPanel!.IsVisible = partsShown; inspectorPanel!.IsVisible = inspectorShown;
        workGrid.ColumnDefinitions[0].Width = new GridLength(partsShown ? 280 : 0);
        workGrid.ColumnDefinitions[2].Width = new GridLength(inspectorShown ? 260 : 0);
        partsToggle!.Content = partsShown ? "Скрыть детали" : "Детали";
        inspectorToggle!.Content = inspectorShown ? "Скрыть свойства" : "Свойства";
        codeToggle!.Content = codeShown ? "Скрыть код" : "Код и монитор";
        if (codeTabs!.IsVisible != codeShown)
        {
            codeTabs.IsVisible = codeShown; codeSplitter!.IsVisible = codeShown;
            splitWorkspace.RowDefinitions[1].Height = codeShown ? sceneHeight : new GridLength(1, GridUnitType.Star);
            splitWorkspace.RowDefinitions[2].Height = new GridLength(codeShown ? 6 : 0);
            splitWorkspace.RowDefinitions[3].MinHeight = codeShown ? 220 : 0;
            splitWorkspace.RowDefinitions[3].Height = codeShown ? codeHeight : new GridLength(0);
        }
    }
}
