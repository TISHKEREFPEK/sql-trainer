using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    readonly ComboBox serialChannel = new(){MinWidth=120};
    readonly ComboBox serialEnding = new(){ItemsSource=new[]{"Без перевода","LF","CRLF"},SelectedIndex=1,MinWidth=125};
    readonly TextBox serialInput = Ui.Input();
    readonly string[] serialText = ["","","",""];
    readonly int[] serialQueued = new int[4];
    readonly TextBlock serialQueueInfo = Ui.Muted("",12);
    Button? serialSend;
    ElectronicsSerialIo? pendingSerial;
    bool serialReady, serialBinding;
    Control BuildSerialPanel()
    {
        serialInput.Watermark="Данные для платы";
        serialInput.MaxLength=4096;
        serialSend=Ui.Button("Отправить",()=>Perform(QueueSerialInput),()=>running&&serialReady&&!ticking&&pendingSerial is null&&!disposed);
        var clear=Ui.Button("Очистить экран",()=>Perform(()=>
        {
            var index=Math.Max(0,serialChannel.SelectedIndex);RecordHistory("serial-clear",true,serialIo:new(index,""));serialText[index]="";ShowSerial();
        }));
        ToolTip.SetTip(clear,"Очищается только экран выбранного порта. Записанная история сохраняется.");
        serialChannel.SelectionChanged+=(_,_)=>
        {
            ShowSerial();if(!serialBinding&&serialChannel.SelectedIndex>=0)_=Perform(()=>RecordHistory("serial-channel",true,serialIo:new(serialChannel.SelectedIndex,"")));
        };
        serialInput.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;_=Perform(QueueSerialInput);}};
        var send=new Grid{ColumnDefinitions=new("*,Auto,Auto"),ColumnSpacing=8};
        send.Children.Add(serialInput);Grid.SetColumn(serialEnding,1);send.Children.Add(serialEnding);Grid.SetColumn(serialSend,2);send.Children.Add(serialSend);
        var panel=new Grid{RowDefinitions=new("Auto,*,Auto"),RowSpacing=8};panel.Children.Add(Ui.Row(serialChannel,clear,serialQueueInfo));Grid.SetRow(monitor,1);panel.Children.Add(monitor);Grid.SetRow(send,2);panel.Children.Add(send);
        UpdateSerialControls();return panel;
    }
    void QueueSerialInput()
    {
        if(!running||!serialReady||disposed)throw new InvalidDataException("Сначала запустите симуляцию платы.");
        if(pendingSerial is not null||ticking)throw new InvalidDataException("Предыдущий обмен с платой ещё обрабатывается. Повторите отправку.");
        var text=(serialInput.Text??"")+(serialEnding.SelectedIndex==1?"\n":serialEnding.SelectedIndex==2?"\r\n":"");
        if(text.Length==0)return;
        try
        {
            var bytes=new System.Text.UTF8Encoding(false,true).GetByteCount(text);
            if(bytes>4096)throw new InvalidDataException("За один раз можно отправить не более 4096 байт.");
            if(bytes+serialQueued[Math.Max(0,serialChannel.SelectedIndex)]>4096)throw new InvalidDataException("Очередь этого порта заполнена. Дождитесь чтения данных программой платы; текст ввода сохранён.");
        }
        catch(System.Text.EncoderFallbackException e){throw new InvalidDataException("Завершите ввод неполного символа.",e);}
        var request=new ElectronicsSerialIo(Math.Max(0,serialChannel.SelectedIndex),text);
        RecordHistory("serial-input-request",true,serialIo:request);pendingSerial=request;serialInput.Text="";UpdateSerialControls();
    }
    void UpdateSerialControls()
    {
        var count=project.Parts.Any(p=>p.Kind==PartKind.Mega)?4:1;
        if(serialChannel.Items.Count!=count)
        {
            serialBinding=true;var selected=serialChannel.SelectedIndex;serialChannel.ItemsSource=Enumerable.Range(0,count).Select(i=>i==0?"Serial":"Serial"+i).ToArray();serialChannel.SelectedIndex=Math.Clamp(selected,0,count-1);serialBinding=false;
        }
        if(serialSend is not null)serialSend.IsEnabled=running&&serialReady&&!ticking&&pendingSerial is null&&!disposed;
        if(codeTabs?.ItemsSource is TabItem[] pages&&pages.Length>0)pages[0].Header="Код "+(project.Parts.Select(AvrBoards.For).FirstOrDefault(b=>b is not null)?.Name??"платы");
        ShowSerial();
    }
    void ShowSerial(){var index=Math.Clamp(serialChannel.SelectedIndex,0,3);monitor.Text=serialText[index];serialQueueInfo.Text=serialQueued[index]>0?$"Ожидает чтения: {serialQueued[index]} байт":"";}
    void ClearSerialDisplay(){Array.Fill(serialText,"");Array.Clear(serialQueued);ShowSerial();}
    void ReceiveSerial(FirmwareFrame frame)
    {
        Array.Clear(serialQueued);if(frame.SerialPending is { }queued)Array.Copy(queued,serialQueued,Math.Min(4,queued.Length));
        var ports=frame.SerialPorts??[frame.Serial];
        for(var i=0;i<Math.Min(4,ports.Length);i++)
        {
            var text=ports[i];if(text.Length==0)continue;
            RecordHistory("serial-output",true,serialIo:new(i,text));
            var displayed=serialText[i]+text;
            if(displayed.Length>40000)displayed="[… предыдущий вывод доступен в истории …]\n"+displayed[^39900..];
            serialText[i]=displayed;
        }
        ShowSerial();
    }
}
