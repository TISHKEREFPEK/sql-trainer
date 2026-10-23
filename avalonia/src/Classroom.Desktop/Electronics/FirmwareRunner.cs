using System.Diagnostics;
using System.Text.Json;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public record FirmwareFrame(PinDrive[] Pins, string Serial, double Milliseconds);

public sealed class FirmwareRunner : IDisposable
{
    Process? process;
    static string Runtime
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CLASSROOM_ELECTRONICS_RUNTIME");
            if (configured is not null) return Path.GetFullPath(configured);
            var deployed = Path.Combine(AppContext.BaseDirectory, "electronics", "runtime");
            if (Directory.Exists(deployed)) return deployed;
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Classroom.sln"))) root = root.Parent;
            return root is null ? deployed : Path.Combine(root.FullName, "artifacts", "electronics-runtime");
        }
    }
    public static bool Available => File.Exists(Path.Combine(Runtime, "arduino-cli.exe")) && File.Exists(Path.Combine(Runtime, "node.exe")) && Directory.Exists(Path.Combine(Runtime, "data", "packages", "arduino", "hardware", "avr", "1.8.6"));

    public static async Task<string> Compile(string code, CancellationToken cancellation)
    {
        if (!Available) throw new IOException("Не установлен автономный комплект Arduino Uno. Подготовьте его командой avalonia/scripts/prepare-electronics.ps1; при работе интернет не требуется.");
        var scratch = Path.Combine(Path.GetTempPath(), "classroom-firmware-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var sketch = Path.Combine(scratch, "sketch"); Directory.CreateDirectory(sketch);
            await File.WriteAllTextAsync(Path.Combine(sketch, "sketch.ino"), code, cancellation);
            var output = Path.Combine(scratch, "compiled");
            var start = new ProcessStartInfo(Path.Combine(Runtime, "arduino-cli.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "compile", "--fqbn", "arduino:avr:uno", "--output-dir", output, "--warnings", "all", sketch }) start.ArgumentList.Add(arg);
            start.Environment["ARDUINO_DIRECTORIES_DATA"] = Path.Combine(Runtime, "data");
            start.Environment["ARDUINO_DIRECTORIES_DOWNLOADS"] = Path.Combine(Runtime, "downloads");
            start.Environment["ARDUINO_DIRECTORIES_USER"] = Path.Combine(Runtime, "user");
            using var compiler = Process.Start(start) ?? throw new IOException("Не удалось запустить компилятор.");
            var stdout = compiler.StandardOutput.ReadToEndAsync(cancellation); var stderr = compiler.StandardError.ReadToEndAsync(cancellation);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation); limit.CancelAfter(TimeSpan.FromSeconds(60));
            try { await compiler.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { if (!compiler.HasExited) compiler.Kill(true); throw new IOException("Компиляция остановлена: отмена или превышен лимит 60 секунд."); }
            var log = (await stdout) + "\n" + (await stderr);
            if (compiler.ExitCode != 0) throw new InvalidDataException("Ошибка компиляции Arduino:\n" + log[..Math.Min(8000, log.Length)]);
            return await File.ReadAllTextAsync(Path.Combine(output, "sketch.ino.hex"), cancellation);
        }
        finally
        {
            var full = Path.GetFullPath(scratch);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("classroom-firmware-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    public async Task Start(string hex)
    {
        Dispose();
        var start = new ProcessStartInfo(Path.Combine(Runtime, "node.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--max-old-space-size=96");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "electronics", "firmware-worker.cjs"));
        process = Process.Start(start) ?? throw new IOException("Не удалось запустить симулятор AVR.");
        process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
        await Exchange<JsonElement>(new { hex });
    }
    public Task<FirmwareFrame> Step(bool[] digital, double[] analog) => Exchange<FirmwareFrame>(new { digital, analog });
    async Task<T> Exchange<T>(object request)
    {
        var active = process ?? throw new IOException("Симулятор не запущен.");
        await active.StandardInput.WriteLineAsync(Wire.Write(request));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var line = await active.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Процесс симулятора остановился.");
        using var doc = JsonDocument.Parse(line);
        if (doc.RootElement.TryGetProperty("error", out var error)) throw new InvalidDataException(error.GetString());
        return Wire.Read<T>(line);
    }
    public void Dispose()
    {
        if (process is null) return;
        if (!process.HasExited) process.Kill(true);
        process.Dispose(); process = null;
    }
}
