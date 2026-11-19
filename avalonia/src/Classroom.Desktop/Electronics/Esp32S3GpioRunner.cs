using System.Globalization;
using System.Text;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public readonly record struct Esp32S3PadState(bool InputHigh, bool Driven, bool OutputHigh, bool PullUp, bool PullDown);
public sealed record Esp32S3SerialFrame(string[] Ports, int[] Pending);

public static class Esp32S3GpioRunner
{
    static readonly SemaphoreSlim slots = new(1, 1);

    public static async Task Run(Esp32S3SketchImage sketch, string runtimePath,
        Func<Esp32S3GpioSession, CancellationToken, Task> simulate, CancellationToken cancellation,
        IReadOnlyDictionary<int, int>? initialAnalog = null)
    {
        ArgumentNullException.ThrowIfNull(simulate);
        if (!OperatingSystem.IsWindows()) throw new IOException("Изоляция ESP32-S3 требует Windows 10 или новее.");
        var flash = Esp32S3Foundation.CopyArduinoFlash(sketch);
        var firstVoltages = Esp32S3GpioSession.CopyAnalogInputs(initialAnalog);
        await slots.WaitAsync(cancellation);
        var scratch = Esp32S3Compiler.Scratch();
        try
        {
            var runtime = await CompilerRuntime.Prepare(runtimePath, cancellation, extraDirectory: "sandbox-xtensa");
            Directory.CreateDirectory(scratch);
            await File.WriteAllBytesAsync(Path.Combine(scratch, "flash.bin"), flash, cancellation);
            List<string> args =
            [
                "-machine", "esp32s3", "-display", "none", "-monitor", "none", "-nic", "none", "-no-reboot",
                "-accel", "tcg,tb-size=32,thread=single",
                "-chardev", "ringbuf,id=classroom-uart0,size=1048576", "-serial", "chardev:classroom-uart0",
                "-chardev", "ringbuf,id=classroom-uart1,size=1048576", "-serial", "chardev:classroom-uart1",
                "-chardev", "ringbuf,id=classroom-uart2,size=1048576", "-serial", "chardev:classroom-uart2",
                "-L", Path.Combine(runtime, "sandbox-xtensa", "qemu", "share", "qemu"),
                "-drive", "file=flash.bin,if=mtd,format=raw", "-qtest", "stdio", "-qtest-log", "NUL"
            ];
            if (firstVoltages.Any(voltage => voltage != 0))
            {
                // Configure the circuit before the guest can read its first sample in setup().
                args.Add("-global");
                args.Add("driver=esp32s3.adc,property=initial-uv,value=" + string.Join(':',
                    firstVoltages.Select(value => value.ToString(CultureInfo.InvariantCulture))));
            }
            await WindowsSandboxProcess.RunControlled(Esp32S3Compiler.Emulator(runtime), args, runtime, scratch,
                async (responses, commands, lifetime) =>
                {
                    var session = new Esp32S3GpioSession(responses, commands, lifetime, firstVoltages);
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
    const string AdcDevice = "/machine/soc/adc";
    const int SerialQueueCapacity = 4096;
    const int SerialReadCapacity = 4096;
    const int UartFifoCapacity = 128;
    static readonly UTF8Encoding strictUtf8 = new(false, true);
    readonly StreamReader responses;
    readonly Stream commands;
    readonly CancellationToken lifetime;
    readonly SemaphoreSlim exchangeGate = new(1, 1);
    readonly Dictionary<int, bool?> previousInputs = [];
    readonly int[] previousAnalog;
    readonly Queue<byte>[] serialInputs = [new(), new(), new()];
    readonly Decoder[] serialDecoders = [Encoding.UTF8.GetDecoder(), Encoding.UTF8.GetDecoder(), Encoding.UTF8.GetDecoder()];
    readonly byte[] serialBytes = new byte[SerialReadCapacity];
    readonly char[] serialChars = new char[Encoding.UTF8.GetMaxCharCount(SerialReadCapacity)];
    bool failed;
    public bool SupportsSerial { get; private set; }
    public bool SupportsAnalog { get; private set; }

    internal Esp32S3GpioSession(StreamReader responses, Stream commands, CancellationToken lifetime, int[] firstVoltages)
    {
        this.responses = responses;
        this.commands = commands;
        this.lifetime = lifetime;
        previousAnalog = firstVoltages;
    }

    internal async Task Probe(CancellationToken cancellation)
    {
        await SetPad(0, null, cancellation);
        SupportsSerial = await ProbeExtension("classroom_uart_version", "UART", cancellation);
        SupportsAnalog = await ProbeExtension("classroom_adc_version", "ADC", cancellation);
    }

    async Task<bool> ProbeExtension(string command, string extension, CancellationToken cancellation)
    {
        var version = await Request(command, cancellation);
        if (version == $"FAIL Unknown command '{command}'") return false;
        if (version != "OK 1") throw new InvalidDataException($"Неизвестная версия протокола {extension} ESP32-S3.");
        return true;
    }

    internal static int[] CopyAnalogInputs(IReadOnlyDictionary<int, int>? inputs)
    {
        var result = new int[Esp32S3Board.AnalogGpioCount];
        if (inputs is null) return result;
        foreach (var (gpio, voltage) in inputs)
        {
            if (!Esp32S3Board.IsAnalogGpio(gpio) || voltage is < 0 or > 3_600_000)
                throw new ArgumentOutOfRangeException(nameof(inputs), "Аналоговый вход ESP32-S3 вне диапазона.");
            result[gpio - 1] = voltage;
        }
        return result;
    }

    public Task<IReadOnlyDictionary<int, Esp32S3PadState>> ExchangePads(
        IReadOnlyDictionary<int, bool?> inputs, CancellationToken cancellation = default,
        IReadOnlyDictionary<int, int>? analog = null)
    {
        var requested = inputs.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (requested.Keys.Any(pin => pin < 0 || pin is >= 22 and <= 25 || pin > 48))
            throw new ArgumentOutOfRangeException(nameof(inputs), "GPIO отсутствует у ESP32-S3.");
        var voltages = analog is not null && SupportsAnalog ? CopyAnalogInputs(analog) : null;
        return Exchange<IReadOnlyDictionary<int, Esp32S3PadState>>(async token =>
        {
            if (voltages is not null)
                for (var slot = 0; slot < voltages.Length; slot++)
                {
                    var voltage = voltages[slot];
                    if (previousAnalog[slot] == voltage) continue;
                    await Send(FormattableString.Invariant($"set_irq_in {AdcDevice} input-uv {slot} {voltage}"), token);
                    previousAnalog[slot] = voltage;
                }
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
        }, cancellation);
    }

    public Task<Esp32S3SerialFrame> ExchangeSerial(ElectronicsSerialIo? input, CancellationToken cancellation = default)
    {
        if (!SupportsSerial) throw new InvalidOperationException("Текущий комплект ESP32-S3 не поддерживает монитор UART.");
        if (input is not null && (input.Port < 0 || input.Port >= serialInputs.Length))
            throw new ArgumentOutOfRangeException(nameof(input), "UART отсутствует у ESP32-S3.");
        byte[] incoming = input is null ? [] : strictUtf8.GetBytes(input.Text);
        return Exchange(async token =>
        {
            if (input is not null)
            {
                var queue = serialInputs[input.Port];
                if (incoming.Length > SerialQueueCapacity - queue.Count)
                    throw new InvalidDataException("Очередь UART ESP32-S3 заполнена.");
                foreach (var value in incoming) queue.Enqueue(value);
            }

            var ports = new string[serialInputs.Length];
            var pending = new int[serialInputs.Length];
            for (var port = 0; port < ports.Length; port++)
            {
                var queue = serialInputs[port];
                if (queue.Count > 0)
                {
                    var bytes = queue.Take(UartFifoCapacity).ToArray();
                    var payload = Convert.ToBase64String(bytes);
                    var write = await Send(FormattableString.Invariant($"classroom_uart_write {port} {payload}"), token);
                    if (write.Length < 4 || !int.TryParse(write.AsSpan(3), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var accepted) || accepted > bytes.Length)
                        throw new InvalidDataException("Некорректное подтверждение приёма UART ESP32-S3.");
                    for (var i = 0; i < accepted; i++) queue.Dequeue();
                }
                pending[port] = queue.Count;
                var reply = await Send(FormattableString.Invariant($"classroom_uart_read {port}"), token);
                var fields = reply.Split(' ');
                if (fields.Length != 3 || !ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var dropped))
                    throw new InvalidDataException("Некорректный вывод UART ESP32-S3.");
                var decoder = serialDecoders[port];
                var marker = "";
                if (dropped > 0)
                {
                    decoder.Reset();
                    marker = $"\n[Переполнение UART: пропущено {dropped} байт]\n";
                }
                var received = 0;
                if (fields[2] != "-" && !Convert.TryFromBase64String(fields[2], serialBytes, out received))
                    throw new InvalidDataException("Некорректные байты вывода UART ESP32-S3.");
                var count = decoder.GetChars(serialBytes.AsSpan(0, received), serialChars, flush: false);
                ports[port] = marker + new string(serialChars, 0, count);
            }
            return new Esp32S3SerialFrame(ports, pending);
        }, cancellation);
    }

    async Task<T> Exchange<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime);
        await exchangeGate.WaitAsync(deadline.Token);
        try
        {
            if (failed) throw new IOException("Связь с ESP32-S3 потеряна. Запустите эмулятор заново.");
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            return await operation(deadline.Token);
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            failed = true;
            throw new IOException("Истекло время обмена с ESP32-S3.");
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
        var reply = await Request(command, cancellation);
        if (reply != "OK" && !reply.StartsWith("OK ", StringComparison.Ordinal))
            throw new InvalidDataException("Эмулятор отклонил команду ESP32-S3.");
        return reply;
    }

    async Task<string> Request(string command, CancellationToken cancellation)
    {
        await commands.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), cancellation).AsTask().WaitAsync(cancellation);
        await commands.FlushAsync(cancellation).WaitAsync(cancellation);
        // Anonymous Win32 pipes can finish a pending read only once the child
        // closes its handle. Releasing the wait lets the owner retire the job.
        var reply = await responses.ReadLineAsync(cancellation).AsTask().WaitAsync(cancellation);
        if (reply is null) throw new EndOfStreamException("Эмулятор закрыл канал ESP32-S3.");
        return reply;
    }
}
