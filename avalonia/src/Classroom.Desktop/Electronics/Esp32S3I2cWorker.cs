using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

internal sealed record Esp32S3I2cDevice(string Id, int Address, int Columns, int Rows,
    bool Connected, bool Powered, string Kind, int Sda, int Scl);
internal sealed record Esp32S3I2cRequest(int Port, uint Epoch, long TimeNs, int Sda, int Scl,
    int Frequency, int RxFree, uint[] Commands, int[] Tx);
internal sealed record Esp32S3I2cReply(int Event, int Commands, int Tx, int[] Rx, bool Busy);
internal sealed record Esp32S3I2cFrame(LcdFrame[] Displays, OledFrame[] OledDisplays, ElectronicsI2cIo[] I2c);

internal sealed class Esp32S3I2cWorker : IDisposable
{
    readonly Process process;
    Esp32S3I2cDevice[] previous = [];

    public Esp32S3I2cWorker()
    {
        var executable = Path.Combine(FirmwareRunner.Runtime, "node.exe");
        if (!File.Exists(executable)) throw new IOException("Для дисплеев ESP32-S3 установите автономный комплект электроники.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--max-old-space-size=96");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "electronics", "esp32s3-i2c-worker.cjs"));
        process = Process.Start(start) ?? throw new IOException("Не удалось запустить модели дисплеев ESP32-S3.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
    }

    public async Task Update(Esp32S3I2cDevice[] devices, CancellationToken cancellation)
    {
        if (devices.SequenceEqual(previous)) return;
        await Exchange<JsonElement>(new { devices }, cancellation);
        previous = devices;
    }

    public Task<Esp32S3I2cReply> Transfer(Esp32S3I2cRequest request, CancellationToken cancellation) =>
        Exchange<Esp32S3I2cReply>(request, cancellation);

    public Task<Esp32S3I2cFrame> Frame(long timeNs, CancellationToken cancellation) =>
        Exchange<Esp32S3I2cFrame>(new { frame = true, timeNs }, cancellation);

    async Task<T> Exchange<T>(object request, CancellationToken cancellation)
    {
        await process.StandardInput.WriteLineAsync(Wire.Write(request).AsMemory(), cancellation);
        var line = await process.StandardOutput.ReadLineAsync(cancellation)
            ?? throw new IOException("Модели дисплеев ESP32-S3 остановились.");
        if (line.Length > 1_000_000) throw new InvalidDataException("Модель дисплея превысила размер ответа.");
        try
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("error", out var error)) throw new InvalidDataException(error.GetString());
            return Wire.Read<T>(line);
        }
        catch (JsonException e) { throw new InvalidDataException("Некорректный ответ модели дисплея.", e); }
    }

    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(true); }
        finally { process.Dispose(); }
    }
}
