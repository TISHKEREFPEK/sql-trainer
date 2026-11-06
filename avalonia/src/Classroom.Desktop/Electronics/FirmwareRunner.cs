using System.Diagnostics;
using System.Text.Json;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public record FirmwareFrame(PinDrive[] Pins, string Serial, double Milliseconds, string[]? SerialPorts = null, int[]? SerialPending = null);

public sealed class FirmwareRunner : IDisposable
{
    static readonly SemaphoreSlim compilerSlots = new(2, 2);
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
    public static bool Available => File.Exists(Path.Combine(Runtime, "arduino-cli.exe")) && File.Exists(Path.Combine(Runtime, "node.exe")) &&
        File.Exists(Path.Combine(Runtime, "sandbox-avr", "bin", "avr-g++.exe")) && File.Exists(Path.Combine(Runtime, "sandbox-avr", "ctags.exe")) &&
        Directory.Exists(Path.Combine(Runtime, "data", "packages", "arduino", "hardware", "avr", "1.8.6"));

    public static async Task<string> Compile(string code, CancellationToken cancellation, string boardId = "uno")
    {
        var board=AvrBoards.Get(boardId);
        if (!Available) throw new IOException("Не установлен автономный комплект Arduino Uno. Подготовьте его командой avalonia/scripts/prepare-electronics.ps1; при работе интернет не требуется.");
        if (!OperatingSystem.IsWindows()) throw new IOException("Изолированная компиляция требует Windows 10 или новее.");
        var scratch = Path.Combine(Path.GetTempPath(), "classroom-firmware-" + Guid.NewGuid().ToString("N"));
        await compilerSlots.WaitAsync(cancellation);
        try
        {
            var runtime = await CompilerRuntime.Prepare(Runtime, cancellation);
            Directory.CreateDirectory(scratch);
            var sketch = Path.Combine(scratch, "sketch"); Directory.CreateDirectory(sketch);
            await File.WriteAllTextAsync(Path.Combine(sketch, "sketch.ino"), code, cancellation);
            var output = Path.Combine(scratch, "compiled");
            var settings = new Dictionary<string, string>
            {
                ["ARDUINO_DIRECTORIES_DATA"] = Path.Combine(runtime, "data"), ["ARDUINO_DIRECTORIES_DOWNLOADS"] = Path.Combine(scratch, "downloads"),
                ["ARDUINO_DIRECTORIES_USER"] = Path.Combine(runtime, "user"), ["ARDUINO_BUILD_CACHE_PATH"] = Path.Combine(scratch, "cache")
            };
            var config = Path.Combine(scratch, "arduino-cli.yaml");
            await File.WriteAllTextAsync(config, Wire.Write(new { metrics = new { enabled = false }, updater = new { enable_notification = false } }), cancellation);
            var args = new[] { "--config-file", config, "--no-color", "compile", "--fqbn", board.Fqbn, "--build-path", Path.Combine(scratch, "build"), "--output-dir", output,
                "--build-property", "compiler.path=" + Path.Combine(runtime, "sandbox-avr", "bin") + Path.DirectorySeparatorChar,
                "--build-property", "runtime.tools.ctags.path=" + Path.Combine(runtime, "sandbox-avr"), "--warnings", "all", "--jobs", "2", sketch };
            var result = await Task.Run(() => OperatingSystem.IsWindows() ? WindowsSandboxProcess.Run(Path.Combine(runtime, "arduino-cli.exe"), args, runtime, scratch, settings, cancellation) : throw new IOException("Изолированная компиляция требует Windows."), cancellation);
            var log = result.Output + "\n" + result.Error;
            if (result.ExitCode != 0) throw new InvalidDataException("Ошибка компиляции Arduino:\n" + log[..Math.Min(8000, log.Length)]);
            return await File.ReadAllTextAsync(Path.Combine(output, "sketch.ino.hex"), cancellation);
        }
        finally
        {
            var full = Path.GetFullPath(scratch);
            try { if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("classroom-firmware-", StringComparison.Ordinal) && Directory.Exists(full)) Directory.Delete(full, true); }
            finally { compilerSlots.Release(); }
        }
    }

    public async Task Start(string hex, string boardId = "uno")
    {
        _=AvrBoards.Get(boardId);
        Dispose();
        var start = new ProcessStartInfo(Path.Combine(Runtime, "node.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.StandardInputEncoding = start.StandardOutputEncoding = start.StandardErrorEncoding = new System.Text.UTF8Encoding(false);
        start.ArgumentList.Add("--max-old-space-size=96");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "electronics", "firmware-worker.cjs"));
        process = Process.Start(start) ?? throw new IOException("Не удалось запустить симулятор AVR.");
        process.ErrorDataReceived += (_, _) => { }; process.BeginErrorReadLine();
        try{await Exchange<JsonElement>(new { hex, board=boardId });}catch{Dispose();throw;}
    }
    public Task<FirmwareFrame> Step(bool[] digital, double[] analog, string? serialInput = null, int serialPort = 0) => Exchange<FirmwareFrame>(new { digital, analog, serialInput, serialPort });
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
