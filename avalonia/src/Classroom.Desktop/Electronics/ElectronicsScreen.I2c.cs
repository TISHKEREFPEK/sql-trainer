using Avalonia.Controls;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    Dictionary<string,LcdFrame> displays=[];
    Dictionary<string,OledFrame> oledDisplays=[];
    void InspectI2c(CircuitPart part)
    {
        var oled=part.Kind==PartKind.OledI2c;var addresses=oled?new[]{60,61}:Enumerable.Range(32,8).Concat(Enumerable.Range(56,8)).ToArray();
        var address=new ComboBox{ItemsSource=addresses.Select(a=>$"0x{a:X2}").ToArray(),SelectedIndex=Array.IndexOf(addresses,(int)part.Value)};
        properties.Children.Add(Ui.Field("Адрес I2C",address));
        properties.Children.Add(Ui.Button("Применить адрес",()=>Perform(()=>
        {
            if(address.SelectedIndex<0)return;Checkpoint();var index=project.Parts.FindIndex(p=>p.Id==part.Id);project.Parts[index]=part with{Value=addresses[address.SelectedIndex]};Refresh();Save("i2c-address");
        })));
        if(project.Parts.Any(p=>p.Kind==PartKind.Pico)){
            var device=CircuitPico.Devices(project,result).FirstOrDefault(d=>d.Id==part.Id);
            properties.Children.Add(Ui.Muted(oled?"Pico: питание OLED от 3V3 и общая GND. SDA/SCL подключите к GPIO и задайте те же номера в коде.":"LCD питается от 5 В; для GPIO Pico 3,3 В требуется согласование уровней. Прямое подключение к подтяжкам 5 В не допускается.",12));
            if(device is not null)properties.Children.Add(Ui.Muted(!device.Powered?"Проверьте питание и общую землю.":!device.Connected?"Проверьте соединения SDA/SCL.":$"SDA — GP{device.Sda}, SCL — GP{device.Scl}.",12));
        }else{
            var device=CircuitI2c.Devices(project,result).FirstOrDefault(d=>d.Id==part.Id);
            properties.Children.Add(Ui.Muted((oled?"Питание модуля: VDD 3.3–5 В и общая GND. ":"Питание: VCC 5 В и общая GND. ")+"SDA/SCL: Uno A4/A5; Mega D20/D21. Подтяжки входят в модель I2C-модуля.",12));
            if(device is not null)properties.Children.Add(Ui.Muted(!device.Powered?"Проверьте питание и общую землю.":!device.Connected?"Проверьте соединения SDA/SCL.":"Питание и шина подключены.",12));
        }
    }
}
