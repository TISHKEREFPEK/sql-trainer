namespace Classroom.Desktop.Electronics;

public static class Hcsr04CodeGuide
{
    public const string Note="HC-SR04: питание 5 В, отдельные Trig/Echo, общая GND. На Pico Echo требует согласования до 3,3 В. pulseIn(...,40000) возвращает 0 при отсутствии цели; повторять измерение не чаще раза в 60 мс.";
    public static string Setup(string trig,string echo)=>$"pinMode({trig},OUTPUT); digitalWrite({trig},LOW); pinMode({echo},INPUT);";
    public static string Loop(string trig,string echo)=>$"{{ digitalWrite({trig},LOW); delayMicroseconds(2); digitalWrite({trig},HIGH); delayMicroseconds(10); digitalWrite({trig},LOW); unsigned long us=pulseIn({echo},HIGH,40000); if(us==0) Serial.println(\"HC-SR04: no echo\"); else {{ Serial.print(\"HC-SR04 cm=\"); Serial.println(us/58.0,1); }} delay(70); }}";
}
