using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Classroom.Desktop;

public sealed partial class StudentScreen
{
    readonly TextBlock saveCaption=Ui.Muted("Состояние сохранения",12);
    readonly TextBlock hintEnd=Ui.Muted("",12);
    readonly TextBlock lessonCaption=Ui.Muted("",12);
    readonly TextBlock connectionCaption=Ui.Muted("",12);
    readonly Popup savePopup=new(){ShouldUseOverlayLayer=true,IsLightDismissEnabled=true,Placement=PlacementMode.Top};
    Action refreshEditing=()=>{};
    Action refreshWorkspacePolicy=()=>{};
    Action resetMaterials=()=>{};
    void RefreshEditingCommands()=>refreshEditing();
    Grid? lessonContext;

    Control CreateSqlHeading()
    {
        var context = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        lessonContext = context;
        context.IsVisible = false;
        context.Children.Add(lessonCaption);
        Grid.SetColumn(connectionCaption, 1);
        context.Children.Add(connectionCaption);
        var headingArea = Ui.Stack(context, heading);
        headingArea.Spacing = 6;
        return headingArea;
    }

    internal void ShowLessonConnection(string? group, bool connected)
    {
        if (connected || string.IsNullOrEmpty(lessonCaption.Text))
            lessonCaption.Text = string.IsNullOrWhiteSpace(group) ? "Занятие" : group;
        lessonCaption.TextTrimming = TextTrimming.CharacterEllipsis;
        lessonCaption.MaxLines = 1;
        connectionCaption.Text = connected ? "Подключено" : "Нет связи с преподавателем";
        if (lessonContext is not null) lessonContext.IsVisible = true;
    }

    Grid CreateSqlWorkspace()
    {
        prompt.FontSize=14;prompt.LineHeight=24.5;
        var materials=Ui.Stack(prompt,tables,hints,nextHint,hintEnd);materials.Spacing=16;
        var conditionTab=new TabItem{Header="Условие",Content=Ui.Scroll(materials)};
        var explanationTab=new TabItem{Header="Объяснение",Content=Ui.Scroll(explanation)};
        var tabs=new TabControl{ItemsSource=new[]{conditionTab,explanationTab},SelectedIndex=0,Padding=new Thickness(0)};
        AutomationProperties.SetName(tabs,"Материалы задания");
        var changingMaterials=false;
        resetMaterials=()=>{changingMaterials=true;try{tabs.SelectedIndex=0;}finally{changingMaterials=false;}};
        tabs.SelectionChanged+=(_,e)=>{if(ReferenceEquals(e.Source,tabs)&&!changingMaterials&&current is not null)RecordHistory(tabs.SelectedIndex==0?"materials-condition":"materials-explanation");};
        restrictionFeedback.FontWeight=FontWeight.SemiBold;
        restrictionNotice.FontSize=12;restrictionFeedback.FontSize=12;pause.FontSize=12;
        var restrictions=new Border{Child=Ui.Scroll(Ui.Stack(restrictionNotice,restrictionFeedback,pause)),Padding=new Thickness(8),MaxHeight=100,CornerRadius=new CornerRadius(10),IsVisible=false};
        Ui.BindBrush(restrictions,Border.BackgroundProperty,"InfoBackground");
        foreach(var text in new[]{restrictionNotice,restrictionFeedback,pause})Ui.BindBrush(text,TextBlock.ForegroundProperty,"InfoInk");
        refreshWorkspacePolicy=()=>
        {
            restrictions.IsVisible=current?.Restricted==true||!string.IsNullOrEmpty(pause.Text)||!string.IsNullOrEmpty(restrictionFeedback.Text);
            restrictionNotice.IsVisible=current?.Restricted==true;
            refreshEditing();
        };
        var left=new Grid{RowDefinitions=new("Auto,*"),RowSpacing=8};left.Children.Add(restrictions);Grid.SetRow(tabs,1);left.Children.Add(tabs);
        var condition=GraphiteUi.Panel(left);condition.Padding=new Thickness(16);condition.MinWidth=0;

        bool CanEdit()=>!disposed&&current is not null&&editor.IsEnabled&&!editor.IsReadOnly;
        var undo=Ui.Button("↶",()=>{TextCommand("undo",()=>editor.Document.UndoStack.Undo());return Task.CompletedTask;},()=>CanEdit()&&editor.Document.UndoStack.CanUndo);
        var redo=Ui.Button("↷",()=>{TextCommand("redo",()=>editor.Document.UndoStack.Redo());return Task.CompletedTask;},()=>CanEdit()&&editor.Document.UndoStack.CanRedo);
        foreach(var (button,name) in new[]{(undo,"Отменить"),(redo,"Повторить")})
        {
            AutomationProperties.SetName(button,name);ToolTip.SetTip(button,name+" последнее изменение");
            button.FontFamily=new FontFamily("Segoe UI Symbol, Segoe UI");button.FontSize=18;button.MinWidth=28;button.MinHeight=28;button.Padding=new Thickness(4);button.BorderThickness=new Thickness(0);
        }
        var reset=Ui.Button("Сбросить",()=>{if(CanEdit())TextCommand("reset",()=>editor.Text=current!.Starter);return Task.CompletedTask;},CanEdit);
        reset.FontSize=12;reset.MinHeight=28;reset.Padding=new Thickness(8,4);ToolTip.SetTip(reset,"Вернуть начальный запрос");
        refreshEditing=()=>{undo.IsEnabled=CanEdit()&&editor.Document.UndoStack.CanUndo;redo.IsEnabled=CanEdit()&&editor.Document.UndoStack.CanRedo;reset.IsEnabled=CanEdit();};
        editor.Document.UndoStack.PropertyChanged+=(_,_)=>refreshEditing();
        var history=Ui.Row(undo,redo,reset);history.Spacing=4;
        var editorTitle=Ui.Text("Запрос SQL",14);editorTitle.FontWeight=FontWeight.SemiBold;editorTitle.VerticalAlignment=VerticalAlignment.Center;
        var header=new Grid{ColumnDefinitions=new("*,Auto"),ColumnSpacing=8};header.Children.Add(editorTitle);Grid.SetColumn(history,1);header.Children.Add(history);
        var editorHeader=new Border{Child=header,Padding=new Thickness(13,10),BorderThickness=new Thickness(0,0,0,1)};
        Ui.BindBrush(editorHeader,Border.BorderBrushProperty,"Line");Ui.BindBrush(editorHeader,Border.BackgroundProperty,"Surface");

        saved.FontSize=14;
        var details=Ui.Stack(saved,resolveConflict,retrySave);
        var detailsScroll=Ui.Scroll(details);detailsScroll.MaxHeight=300;
        var detailsPanel=GraphiteUi.Panel(detailsScroll);detailsPanel.Width=400;
        owner.ConfigureStudentPopup(detailsPanel);savePopup.Child=detailsPanel;
        var state=Ui.Button("",()=>
        {
            detailsPanel.Width=Math.Max(180,Math.Min(400,owner.ClientSize.Width-40));
            detailsScroll.MaxHeight=Math.Max(100,owner.ClientSize.Height-100);
            savePopup.IsOpen=!savePopup.IsOpen;return Task.CompletedTask;
        });
        state.MinHeight=34;state.Padding=new Thickness(0);state.BorderThickness=new Thickness(0);state.HorizontalContentAlignment=HorizontalAlignment.Left;
        var saveContent=new Grid{ColumnDefinitions=new("*,Auto"),ColumnSpacing=4};saveContent.Children.Add(saveCaption);
        var detailsGlyph=Ui.Muted("⌃",12);detailsGlyph.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(detailsGlyph,1);saveContent.Children.Add(detailsGlyph);
        state.Content=saveContent;state.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        AutomationProperties.SetName(state,"Состояние сохранения");
        saved.PropertyChanged+=(_,e)=>{if(e.Property==TextBlock.TextProperty){RefreshSaveCaption();AutomationProperties.SetName(state,"Сохранение: "+saved.Text);}};
        state.Background=Brushes.Transparent;ToolTip.SetTip(state,"Состояние сохранения — нажмите для подробностей");savePopup.PlacementTarget=state;
        detailsPanel.AddHandler(KeyDownEvent,(_,e)=>{if(e.Key==Key.Escape){savePopup.IsOpen=false;state.Focus();e.Handled=true;}},RoutingStrategies.Tunnel);
        var actions=Ui.Row(run,check);actions.Spacing=8;
        foreach(var button in new[]{run,check}){button.FontSize=12;button.MinHeight=34;button.Padding=new Thickness(10,7);}
        var footer=new Grid{ColumnDefinitions=new("*,Auto"),ColumnSpacing=8};footer.Children.Add(state);Grid.SetColumn(actions,1);footer.Children.Add(actions);
        var editorFooter=new Border{Child=footer,Padding=new Thickness(12,10),BorderThickness=new Thickness(0,1,0,0)};
        Ui.BindBrush(editorFooter,Border.BorderBrushProperty,"Line");Ui.BindBrush(editorFooter,Border.BackgroundProperty,"Surface");
        saveCaption.MaxLines=2;saveCaption.TextTrimming=TextTrimming.CharacterEllipsis;
        var code=new Grid{RowDefinitions=new("Auto,*,Auto")};code.Children.Add(editorHeader);Grid.SetRow(editor,1);code.Children.Add(editor);Grid.SetRow(editorFooter,2);code.Children.Add(editorFooter);code.Children.Add(savePopup);
        editor.Padding=new Thickness(13,10);editor.MinHeight=0;
        var editorPanel=GraphiteUi.Panel(code);editorPanel.Padding=new Thickness(0);editorPanel.ClipToBounds=true;editorPanel.MinWidth=0;

        var resultHeader=new Grid{ColumnDefinitions=new("*,Auto"),ColumnSpacing=8};var resultTitle=Ui.Text("Результат",14);resultTitle.FontWeight=FontWeight.SemiBold;
        resultHeader.Children.Add(resultTitle);Grid.SetColumn(resultCount,1);resultHeader.Children.Add(resultCount);
        var resultLayout=new Grid{RowDefinitions=new("Auto,*,Auto"),RowSpacing=10};resultLayout.Children.Add(resultHeader);Grid.SetRow(result,1);resultLayout.Children.Add(result);Grid.SetRow(resultChanged,2);resultLayout.Children.Add(resultChanged);
        resultChanged.FontSize=12;Ui.BindBrush(resultChanged,TextBlock.ForegroundProperty,"StaleResultInk");
        var resultPanel=GraphiteUi.Panel(resultLayout);resultPanel.Padding=new Thickness(15,13,15,11);resultPanel.MinWidth=0;
        var right=new Grid{RowDefinitions=new("0.537*,14,0.463*")};right.Children.Add(editorPanel);Grid.SetRow(resultPanel,2);right.Children.Add(resultPanel);
        var splitter=new GridSplitter{ResizeDirection=GridResizeDirection.Rows,ResizeBehavior=GridResizeBehavior.PreviousAndNext,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Center,Height=6,Background=Brushes.Transparent};
        AutomationProperties.SetName(splitter,"Высота редактора и результата");Grid.SetRow(splitter,1);right.Children.Add(splitter);
        var workspace=new Grid{ColumnDefinitions=new("0.415*,0.585*"),ColumnSpacing=16};workspace.Children.Add(condition);Grid.SetColumn(right,1);workspace.Children.Add(right);
        workspace.SizeChanged+=(_,_)=>
        {
            var height=workspace.Bounds.Height;
            editorPanel.MinHeight=Math.Min(160,Math.Max(100,height-78));
            resultPanel.MinHeight=Math.Min(64,Math.Max(24,height*.3));
        };
        DetachedFromVisualTree+=(_,_)=>savePopup.IsOpen=false;
        refreshEditing();ResetSqlResult();return workspace;
    }

    void RefreshSaveCaption()
    {
        if(current is null||local is null)return;
        var state=local.Find(lessonId!,workScope,current.Id);
        var caption=state is null?"Нет локальной копии":state.Code!=editor.Text?"Не сохранено на ПК"
            :saved.Text?.StartsWith("Не удалось",StringComparison.Ordinal)==true?"Ошибка сохранения"
            :state?.Conflict is not null?"Конфликт черновика"
            :state?.Error is not null?"Ошибка передачи"
            :state?.Dirty==true||state?.PendingDraft is not null?"Ожидает передачи"
            :state?.Task.Draft.Length>0?"Сохранено на ПК и у преподавателя":"Сохранено на ПК";
        if(state?.Execution is not null)caption+=executing?"; запрос выполняется":"; повтор SQL";
        else if(state is not null&&HasHistoryToSend(state))caption+="\nИстория ожидает передачи";
        saveCaption.Text=caption;AutomationProperties.SetName(saveCaption,"Сохранение: "+saved.Text);
        UpdateResultStale();refreshEditing();
    }
}
