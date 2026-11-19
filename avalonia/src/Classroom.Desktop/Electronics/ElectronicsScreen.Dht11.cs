namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    void InspectDht11(CircuitPart p)
    {
        properties.Children.Add(Ui.Text("DHT11: VDD 3–5,5 В, DATA, NC (не подключать), GND. Рекомендуется резистор 5,1 кОм от DATA к питанию. Для Pico используйте 3,3 В. После включения нужен прогрев 1 с; библиотека читает не чаще раза в 2 с.",12));
        AddInspectorSlider(p, dhtTemperature);
        AddInspectorSlider(p, humidity);
        properties.Children.Add(Ui.Muted("Можно менять во время симуляции. Датчик возвращает предыдущее измерение; новое значение появится после следующего цикла чтения.",12));
    }
}
