using System.Globalization;
using System.Text;

namespace Classroom.Desktop.Electronics;

public readonly record struct Esp32S3PadState(bool InputHigh, bool Driven, bool OutputHigh, bool PullUp, bool PullDown);

public static class Esp32S3GpioRunner
{
    static readonly SemaphoreSlim slots = new(1, 1);

    public static async Task Run(Esp32S3SketchImage sketch, string runtimePath,
        Func<Esp32S3GpioSession, CancellationToken, Task> simulate, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(simulate);
        if (!OperatingSystem.IsWindows()) throw new IOException("Изоляция ESP32-S3 требует Windows 10 или новее.");
        var flash = Esp32S3Foundation.CopyArduinoFlash(sketch);
        await slots.WaitAsync(cancellation);
        var scratch = Esp32S3Compiler.Scratch();
        try
        {
            var runtime = await CompilerRuntime.Prepare(runtimePath, cancellation, extraDirectory: "sandbox-xtensa");
            Directory.CreateDirectory(scratch);
            await File.WriteAllBytesAsync(Path.Combine(scratch, "flash.bin"), flash, cancellation);
            string[] args =
            [
                "-machine", "esp32s3", "-display", "none", "-monitor", "none", "-nic", "none", "-no-reboot",
                "-accel", "tcg,tb-size=32,thread=single", "-serial", "null", "-serial", "null",
                "-L", Path.Combine(runtime, "sandbox-xtensa", "qemu", "share", "qemu"),
                "-drive", "file=flash.bin,if=mtd,format=raw", "-qtest", "stdio", "-qtest-log", "NUL"
            ];
            await WindowsSandboxProcess.RunControlled(Esp32S3Compiler.Emulator(runtime), args, runtime, scratch,
                async (responses, commands, lifetime) =>
                {
                    var session = new Esp32S3GpioSession(responses, commands, lifetime);
                    using (var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
                    {
                        startup.CancelAfter(TimeSpan.FromSeconds(5));
                        // Releasing the default floating pad confirms that this
                        // QEMU exposes the real pad interface, even on a blank board.
                        await session.Probe(startup.Token);
                    }
                    await simulate(session, lifetime);
                }, cancellation);
        }
        finally
        {
            try { Esp32S3Compiler.CleanScratch(scratch); }
            finally { slots.Release(); }
        }
    }
}

public sealed class Esp32S3GpioSession
{
    const uint Gpio = 0x60004000;
    const uint Iomux = 0x60009000;
    const string PadDevice = "/machine/soc/gpio";
    readonly StreamReader responses;
    readonly Stream commands;
    readonly CancellationToken lifetime;
    readonly SemaphoreSlim exchangeGate = new(1, 1);
    readonly Dictionary<int, bool?> previousInputs = [];
    bool failed;

    internal Esp32S3GpioSession(StreamReader responses, Stream commands, CancellationToken lifetime)
    {
        this.responses = responses;
        this.commands = commands;
        this.lifetime = lifetime;
    }

    internal Task Probe(CancellationToken cancellation) => SetPad(0, null, cancellation);

    public async Task<IReadOnlyDictionary<int, Esp32S3PadState>> ExchangePads(
        IReadOnlyDictionary<int, bool?> inputs, CancellationToken cancellation = default)
    {
        var requested = inputs.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (requested.Keys.Any(pin => pin < 0 || pin is >= 22 and <= 25 || pin > 48))
            throw new ArgumentOutOfRangeException(nameof(inputs), "GPIO отсутствует у ESP32-S3.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime);
        await exchangeGate.WaitAsync(deadline.Token);
        try
        {
            if (failed) throw new IOException("Связь с GPIO потеряна. Запустите эмулятор заново.");
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var token = deadline.Token;
            foreach (var (pin, old) in previousInputs.ToArray())
            {
                if (requested.ContainsKey(pin)) continue;
                if (old is not null) await SetPad(pin, null, token);
                previousInputs.Remove(pin);
            }
            foreach (var (pin, value) in requested)
            {
                if (previousInputs.GetValueOrDefault(pin) == value) continue;
                await SetPad(pin, value, token);
                previousInputs[pin] = value;
            }

            var states = new Dictionary<int, Esp32S3PadState>();
            foreach (var bank in requested.Keys.GroupBy(pin => pin / 32))
            {
                var output = await ReadRegister(Gpio + (bank.Key == 0 ? 0x04u : 0x10u), token);
                var enable = await ReadRegister(Gpio + (bank.Key == 0 ? 0x20u : 0x2cu), token);
                var input = await ReadRegister(Gpio + (bank.Key == 0 ? 0x3cu : 0x40u), token);
                foreach (var pin in bank)
                {
                    var mux = await ReadRegister(Iomux + 4u * (uint)(pin + 1), token);
                    var pinConfig = await ReadRegister(Gpio + 0x74 + 4u * (uint)pin, token);
                    var route = await ReadRegister(Gpio + 0x554 + 4u * (uint)pin, token);
                    var mask = 1u << (pin % 32);
                    var high = ((output & mask) != 0) ^ ((route & 0x200) != 0);
                    var outputEnabled = ((enable & mask) != 0) ^ ((route & 0x800) != 0);
                    var gpioOutput = ((mux >> 12) & 7) == 1 && (route & 0x1ff) == 256;
                    var driven = gpioOutput && outputEnabled && !(high && (pinConfig & 4) != 0);
                    states.Add(pin, new((input & mask) != 0, driven, high, (mux & 0x100) != 0, (mux & 0x80) != 0));
                }
            }
            return states;
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            failed = true;
            throw new IOException("Истекло время обмена с GPIO ESP32-S3.");
        }
        catch
        {
            failed = true;
            throw;
        }
        finally { exchangeGate.Release(); }
    }

    Task<string> SetPad(int pin, bool? level, CancellationToken cancellation)
    {
        var tag = level is null ? -1 : level.Value ? 1 : 0;
        return Send(FormattableString.Invariant($"set_irq_in {PadDevice} pad-in {pin} {tag}"), cancellation);
    }

    async Task<uint> ReadRegister(uint address, CancellationToken cancellation)
    {
        var reply = await Send(FormattableString.Invariant($"readl 0x{address:x}"), cancellation);
        if (!reply.StartsWith("OK 0x", StringComparison.Ordinal) ||
            !uint.TryParse(reply.AsSpan(5), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException("Некорректное значение регистра ESP32-S3.");
        return value;
    }

    async Task<string> Send(string command, CancellationToken cancellation)
    {
        await commands.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), cancellation).AsTask().WaitAsync(cancellation);
        await commands.FlushAsync(cancellation).WaitAsync(cancellation);
        // Anonymous Win32 pipes can finish a pending read only once the child
        // closes its handle. Releasing the wait lets the owner retire the job.
        var reply = await responses.ReadLineAsync(cancellation).AsTask().WaitAsync(cancellation);
        if (reply is null) throw new EndOfStreamException("Эмулятор закрыл канал GPIO.");
        if (reply != "OK" && !reply.StartsWith("OK ", StringComparison.Ordinal))
            throw new InvalidDataException("Эмулятор отклонил команду GPIO.");
        return reply;
    }
}
