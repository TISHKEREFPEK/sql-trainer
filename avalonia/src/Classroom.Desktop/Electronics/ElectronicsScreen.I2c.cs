using Avalonia.Controls;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    Dictionary<string,LcdFrame> displays=[];
    void InspectLcd(CircuitPart part)
    {
        var addresses=Enumerable.Range(32,8).Concat(Enumerable.Range(56,8)).ToArray();
        var address=new ComboBox{ItemsSource=addresses.Select(a=>$"0x{a:X2}").ToArray(),SelectedIndex=Array.IndexOf(addresses,(int)part.Value)};
        properties.Children.Add(Ui.Field("Адрес I2C",address));
        properties.Children.Add(Ui.Button("Применить адрес",()=>Perform(()=>
        {
            if(address.SelectedIndex<0)return;Checkpoint();var index=project.Parts.FindIndex(p=>p.Id==part.Id);project.Parts[index]=part with{Value=addresses[address.SelectedIndex]};Refresh();Save("i2c-address");
        })));
        var device=CircuitI2c.Devices(project,result).FirstOrDefault(d=>d.Id==part.Id);
        properties.Children.Add(Ui.Muted("Питание: VCC 5 В и общая GND. SDA/SCL: Uno A4/A5; Mega D20/D21. Подтяжки входят в модель I2C-модуля.",12));
        if(device is not null)properties.Children.Add(Ui.Muted(!device.Powered?"Проверьте питание и общую землю.":!device.Connected?"Проверьте соединения SDA/SCL.":"Питание и шина подключены.",12));
    }
}
