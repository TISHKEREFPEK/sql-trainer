using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Path=Avalonia.Controls.Shapes.Path;

namespace Classroom.Desktop;

sealed class StudentNavigation : Border
{
    protected override Type StyleKeyOverride=>typeof(Border);
    readonly Dictionary<string,(Button Button,TextBlock Label)> entries=new();
    readonly ContentControl work=new(),progress=new(),utilities=new();
    readonly TextBlock brand=Ui.Text("Classroom",16),name=Ui.Text("",13),group=Ui.Muted("",12),initials=Ui.Text("",13);
    readonly Button profileMenu;
    readonly Popup profilePopup=new(){ShouldUseOverlayLayer=true,IsLightDismissEnabled=true,Placement=PlacementMode.Right,HorizontalOffset=6},
        taskPopup=new(){ShouldUseOverlayLayer=true,IsLightDismissEnabled=true,Placement=PlacementMode.Right,HorizontalOffset=6};
    readonly ContentControl profilePopupContent=new(),taskPopupContent=new();
    public Popup CataloguePopup=>taskPopup;
    Control? requestedWork,requestedSummary;
    Control? utilityContent;
    bool compact,busy,overlay,canWork=true,allowSql=true,allowElectronics=true;
    public bool IsBusy=>busy;
    public event Action<bool>? BusyChanged;

    public StudentNavigation(Func<string,Task> navigate,Action<Control> configurePopup)
    {
        Classes.Add("student-navigation");
        var brandMark=Icon("M2,6 L9,2 L16,6 L9,10 Z M2,10 L9,14 L16,10 M2,14 L9,18 L16,14");
        brandMark.StrokeThickness=1.6;
        Ui.BindBrush(brandMark,Path.StrokeProperty,"AccentInk");
        var logo=new Border{Child=brandMark,Width=28,Height=28,CornerRadius=new CornerRadius(8)};Ui.BindBrush(logo,BackgroundProperty,"Accent");
        var brandRow=new Grid{ColumnDefinitions=new("28,*"),ColumnSpacing=8,Margin=new Thickness(0,8,0,2)};
        brand.FontWeight=FontWeight.SemiBold;brand.VerticalAlignment=VerticalAlignment.Center;
        brandRow.Children.Add(logo);Grid.SetColumn(brand,1);brandRow.Children.Add(brand);
        var menu=new StackPanel{Spacing=6};
        Button Add(string key,string label,string geometry)
        {
            var title=Ui.Text(label,13);title.VerticalAlignment=VerticalAlignment.Center;
            var icon=Icon(geometry);
            var content=new Grid{ColumnDefinitions=new("18,*"),ColumnSpacing=8};content.Children.Add(icon);Grid.SetColumn(title,1);content.Children.Add(title);
            var button=Ui.Button("",async()=>
            {
                if(key=="tasks"){taskPopup.IsOpen=!taskPopup.IsOpen;return;}
                if(busy||!Allowed(key))return;
                SetBusy(true);try{await navigate(key);}finally{SetBusy(false);}
            },()=>!busy&&Allowed(key));
            button.Content=content;button.Classes.Add("student-nav");
            icon.Bind(Path.StrokeProperty,new Avalonia.Data.Binding("Foreground"){Source=button});
            button.Padding=new Thickness(8,7);button.MinHeight=38;button.HorizontalAlignment=HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            AutomationProperties.SetName(button,label);ToolTip.SetTip(button,label);
            entries.Add(key,(button,title));menu.Children.Add(button);return button;
        }
        Add("home","Моё занятие","M2,2 H7 V7 H2 Z M11,2 H16 V7 H11 Z M2,11 H7 V16 H2 Z M11,11 H16 V16 H11 Z");
        Add("sql","SQL","M2,3 H16 V15 H2 Z M5,7 L8,9 L5,11 M10,12 H14");
        var taskMenu=Add("tasks","Выбрать задание","M2,4 H4 M7,4 H16 M2,9 H4 M7,9 H16 M2,14 H4 M7,14 H16");
        taskPopup.PlacementTarget=taskMenu;taskMenu.IsVisible=false;
        Add("electronics","Электроника","M5,5 H13 V13 H5 Z M2,6 H5 M2,10 H5 M13,6 H16 M13,10 H16 M6,2 V5 M10,2 V5 M6,13 V16 M10,13 V16");
        Add("glossary","Справочник","M9,4 V16 M9,4 Q5,1 1,3 V15 Q5,13 9,16 Q13,13 17,15 V3 Q13,1 9,4");
        var review=Add("review","Проверка преподавателя","M4,2 H14 V16 H4 Z M7,6 H11 M7,10 L9,12 L13,8");
        entries["review"].Label.FontSize=12;ToolTip.SetTip(review,"Одноразовый доступ преподавателя к проверке работ");
        var profile=new Grid{ColumnDefinitions=new("28,*"),ColumnSpacing=8};
        var avatar=new Border{Child=initials,CornerRadius=new CornerRadius(14),Width=28,Height=28};
        Ui.BindBrush(avatar,BackgroundProperty,"SelectedTask");initials.HorizontalAlignment=HorizontalAlignment.Center;initials.VerticalAlignment=VerticalAlignment.Center;
        var identity=Ui.Stack(name,group);identity.Spacing=2;identity.Name="StudentIdentity";
        profile.Children.Add(avatar);Grid.SetColumn(identity,1);profile.Children.Add(identity);
        profileMenu=Ui.Button("",()=>{profilePopup.IsOpen=!profilePopup.IsOpen;return Task.CompletedTask;});profileMenu.Content=Icon("M3,5 H15 M3,9 H15 M3,13 H15");
        profileMenu.MinWidth=28;profileMenu.Padding=new Thickness(6);profileMenu.IsVisible=false;
        AutomationProperties.SetName(profileMenu,"Меню профиля");ToolTip.SetTip(profileMenu,"Меню профиля");profilePopup.PlacementTarget=profileMenu;
        var footer=Ui.Stack(profile,utilities,profileMenu);footer.Spacing=10;
        utilities.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        work.HorizontalContentAlignment=HorizontalAlignment.Stretch;work.VerticalContentAlignment=VerticalAlignment.Stretch;
        name.MaxHeight=42;name.TextTrimming=TextTrimming.CharacterEllipsis;group.MaxHeight=36;group.TextTrimming=TextTrimming.CharacterEllipsis;
        var menuScroll=Ui.Scroll(menu);
        var layout=new Grid{RowDefinitions=new("Auto,Auto,*,Auto,Auto"),RowSpacing=14};
        layout.Children.Add(brandRow);Grid.SetRow(menuScroll,1);layout.Children.Add(menuScroll);
        Grid.SetRow(work,2);layout.Children.Add(work);Grid.SetRow(progress,3);layout.Children.Add(progress);
        Grid.SetRow(footer,4);layout.Children.Add(footer);Child=layout;
        void SetupPopup(Popup popup,ContentControl content,Button trigger)
        {
            var panel=GraphiteUi.Panel(content);panel.MinWidth=180;
            popup.Child=panel;layout.Children.Add(popup);configurePopup(panel);
            panel.AddHandler(KeyDownEvent,(_,e)=>{if(e.Key==Key.Escape){popup.IsOpen=false;trigger.Focus();e.Handled=true;}},RoutingStrategies.Tunnel);
        }
        SetupPopup(taskPopup,taskPopupContent,taskMenu);SetupPopup(profilePopup,profilePopupContent,profileMenu);
        SizeChanged+=(_,_)=>
        {
            menuScroll.MaxHeight=Bounds.Width<100?Math.Max(76,Bounds.Height-220):double.PositiveInfinity;
            if(requestedWork is not null&&compact)requestedWork.MaxHeight=Math.Max(160,(TopLevel.GetTopLevel(this)?.ClientSize.Height??567)-100);
            var next=Bounds.Width<100;if(compact==next)return;compact=next;
            Padding=new Thickness(compact?6:12,12);
            brand.IsVisible=identity.IsVisible=!compact;progress.IsVisible=!compact;
            foreach(var entry in entries.Values)
            {
                entry.Label.IsVisible=!compact;
                ((Grid)entry.Button.Content!).ColumnSpacing=compact?0:8;
                ((Grid)entry.Button.Content!).ColumnDefinitions[1].Width=compact?new GridLength(0):new GridLength(1,GridUnitType.Star);
                entry.Button.Padding=new Thickness(compact?6:8,7);
                entry.Button.HorizontalContentAlignment=compact?HorizontalAlignment.Center:HorizontalAlignment.Stretch;
            }
            // A native flyout retains the actual catalogue controls in compact
            // mode, instead of squeezing task names into a 52-DIP rail.
            work.IsVisible=!compact;
            profileMenu.IsVisible=compact;MoveUtilities();MoveWork();
        };
    }

    static Path Icon(string geometry)
    {
        var icon=new Path{Data=Geometry.Parse(geometry),Width=18,Height=18,Stretch=Stretch.Uniform,StrokeThickness=1.6,VerticalAlignment=VerticalAlignment.Center};
        Ui.BindBrush(icon,Path.StrokeProperty,"Ink");return icon;
    }
    bool Allowed(string key)=>key is "home" or "review"||canWork&&(key=="electronics"?allowElectronics:allowSql)&&(key!="tasks"||!overlay);
    public Button Entry(string key)=>entries[key].Button;
    public void SetPermission(bool allowed,bool sql=true,bool electronics=true){canWork=allowed;allowSql=sql;allowElectronics=electronics;ApplyAvailability();}
    public void SetOverlay(bool value){overlay=value;if(value){taskPopup.IsOpen=profilePopup.IsOpen=false;}ApplyAvailability();}
    public void SetBusy(bool value){if(busy==value)return;busy=value;if(value){taskPopup.IsOpen=profilePopup.IsOpen=false;}ApplyAvailability();BusyChanged?.Invoke(value);}
    void ApplyAvailability()
    {
        foreach(var (key,entry) in entries)entry.Button.IsEnabled=!busy&&Allowed(key);
        work.IsEnabled=!busy&&!overlay&&canWork&&allowSql;utilities.IsEnabled=profileMenu.IsEnabled=!busy;
        if(requestedWork is not null)requestedWork.IsEnabled=!busy&&!overlay&&canWork&&allowSql;
        if(utilityContent is not null)utilityContent.IsEnabled=!busy;
    }
    public void Select(string key){foreach(var (id,entry) in entries)entry.Button.Classes.Set("active",id==key);}
    public void SetWork(Control? navigation,Control? summary)
    {
        if(ReferenceEquals(requestedWork,navigation)&&ReferenceEquals(requestedSummary,summary))return;
        if(requestedWork is not null){requestedWork.MinWidth=0;requestedWork.MaxHeight=double.PositiveInfinity;}
        work.Content=null;taskPopup.IsOpen=false;taskPopupContent.Content=null;
        requestedWork=navigation;requestedSummary=summary;MoveWork();
    }
    void MoveWork()
    {
        work.Content=null;taskPopup.IsOpen=false;taskPopupContent.Content=null;
        entries["tasks"].Button.IsVisible=compact&&requestedWork is not null;
        if(requestedWork is not null)
        {
            requestedWork.MinWidth=compact?240:0;
            requestedWork.MaxHeight=compact?Math.Max(160,(TopLevel.GetTopLevel(this)?.ClientSize.Height??567)-100):double.PositiveInfinity;
        }
        if(compact)taskPopupContent.Content=requestedWork;else work.Content=requestedWork;
        if(!ReferenceEquals(progress.Content,requestedSummary))progress.Content=requestedSummary;
    }
    public void SetUtilities(Control? content)
    {
        utilities.Content=null;profilePopup.IsOpen=false;profilePopupContent.Content=null;utilityContent=content;MoveUtilities();
    }
    void MoveUtilities()
    {
        utilities.Content=null;profilePopup.IsOpen=false;profilePopupContent.Content=null;
        if(compact)profilePopupContent.Content=utilityContent;else utilities.Content=utilityContent;
    }
    public void Profile(string fullName,string groupName)
    {
        name.Text=fullName;group.Text=groupName;
        ToolTip.SetTip(name,fullName);ToolTip.SetTip(group,groupName);
        initials.Text=string.Concat(fullName.Split(' ',StringSplitOptions.RemoveEmptyEntries).Take(2).Select(part=>System.Globalization.StringInfo.GetNextTextElement(part))).ToUpperInvariant();
        ToolTip.SetTip(initials,fullName+" · "+groupName);
    }
}
