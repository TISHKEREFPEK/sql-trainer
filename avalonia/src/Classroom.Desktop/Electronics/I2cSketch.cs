namespace Classroom.Desktop.Electronics;

public static class I2cSketch
{
    public const string Helpers="""
        #include <Wire.h>
        // PCF8574: RS=P0, RW=P1, E=P2, подсветка=P3, DB4..7=P4..7.
        void lcdNibble(byte address, byte value, bool data = false) {
          byte pins = (value << 4) | 8 | (data ? 1 : 0);
          Wire.beginTransmission(address);
          Wire.write(pins); Wire.write(pins | 4); Wire.write(pins);
          Wire.endTransmission();
        }
        void lcdSend(byte address, byte value, bool data = false) {
          lcdNibble(address, value >> 4, data);
          lcdNibble(address, value & 15, data);
          delayMicroseconds(50);
        }
        void lcdBegin(byte address) {
          delay(50);
          lcdNibble(address, 3); delay(5);
          lcdNibble(address, 3); delayMicroseconds(200);
          lcdNibble(address, 3); delayMicroseconds(200);
          lcdNibble(address, 2);
          lcdSend(address, 0x28); lcdSend(address, 0x0C);
          lcdSend(address, 0x01); delay(2); lcdSend(address, 0x06);
        }
        void lcdText(byte address, const char* text) {
          while (*text) lcdSend(address, *text++, true);
        }

        """;
}
