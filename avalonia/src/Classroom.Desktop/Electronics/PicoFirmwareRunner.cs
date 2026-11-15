using System.Diagnostics;
using System.Text.Json;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed record PicoI2cDevice(string Id,int Address,int Columns,int Rows,bool Connected,bool Powered,string Kind="lcd",int Controller=0,int Sda=4,int Scl=5);
public sealed record PicoI2cIo(int Controller,int Address,bool Read,int[] Data,bool Ack,bool RepeatedStart,bool Software=false,int? Sda=null,int? Scl=null);
public sealed record PicoFrame(PinDrive[] Pins,string[] SerialPorts,int[] SerialPending,bool UsbReady,double Milliseconds,bool Complete,long Instructions,double ClockHz,string[] Diagnostics,LcdFrame[] Displays,OledFrame[] OledDisplays,PicoI2cIo[] I2c,int? ResetReason=null,TftDisplayFrame[]? TftDisplays=null,ElectronicsSpiIo[]? Spi=null,Ds18b20Frame[]? Ds18b20=null,OneWireMemoryWrite[]? OneWireWrites=null);

// Foundation API, deliberately separate from the supported circuit-board picker.
public sealed class PicoFirmwareRunner : IDisposable
{
    Process? process;
    readonly SemaphoreSlim exchange=new(1,1);
    public async Task Start(byte[] uf2)
    {
        if(uf2 is null||uf2.Length>4*1024*1024)throw new InvalidDataException("Неверный размер прошивки Pico.");
        Dispose();var start=new ProcessStartInfo(Path.Combine(PicoCompiler.Runtime,"node.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.StandardInputEncoding=start.StandardOutputEncoding=start.StandardErrorEncoding=new System.Text.UTF8Encoding(false);
        start.ArgumentList.Add("--max-old-space-size=96");start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"electronics","pico-worker.cjs"));
        process=Process.Start(start)??throw new IOException("Не удалось запустить основу симулятора Pico.");
        process.ErrorDataReceived+=(_,_)=>{};process.BeginErrorReadLine();
        try{await Exchange<JsonElement>(new{uf2=Convert.ToBase64String(uf2)});}catch{Dispose();throw;}
    }
    public Task<PicoFrame> Step(bool[]? digital=null,double[]? analog=null,string? serialInput=null,int serialPort=0,double milliseconds=10,IReadOnlyList<PicoI2cDevice>? devices=null,bool?[]? gpioInputs=null,IReadOnlyList<SpiDisplayDevice>? spiDevices=null,IReadOnlyList<Dht11Device>? dht11Devices=null,IReadOnlyList<Hcsr04Device>? hcsr04Devices=null,IReadOnlyList<Ds18b20Device>? ds18b20Devices=null)=>Exchange<PicoFrame>(new{digital,analog,serialInput,serialPort,milliseconds,devices,gpioInputs,spiDevices,dht11Devices,hcsr04Devices,ds18b20Devices});
    async Task<T> Exchange<T>(object request)
    {
        await exchange.WaitAsync();
        try
        {
            var active=process??throw new IOException("Симулятор Pico не запущен.");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));await active.StandardInput.WriteLineAsync(Wire.Write(request).AsMemory(),timeout.Token);
            var line=await active.StandardOutput.ReadLineAsync(timeout.Token)??throw new IOException("Процесс Pico остановился.");
            if(line.Length>1000000)throw new InvalidDataException("Симулятор Pico превысил размер ответа.");
            using var document=JsonDocument.Parse(line);if(document.RootElement.TryGetProperty("error",out var error))throw new InvalidDataException(error.GetString());
            return Wire.Read<T>(line);
        }
        catch(OperationCanceledException){Dispose();throw new IOException("Превышено время ответа симулятора Pico.");}
        finally{exchange.Release();}
    }
    public void Dispose()
    {var active=process;process=null;if(active is null)return;try{if(!active.HasExited)active.Kill(true);}finally{active.Dispose();}}
}
