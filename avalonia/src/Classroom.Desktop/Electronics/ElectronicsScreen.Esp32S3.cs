using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    Task? s3Execution;
    Esp32S3GpioSession? s3Session;
    Esp32S3I2cWorker? s3I2c;
    int[] s3WiredPins = [];

    async Task StartEsp32S3(CircuitPart board, Esp32S3SketchImage image, string runtimePath, long generation, CancellationToken cancellation)
    {
        var connected = new TaskCompletionSource<Esp32S3GpioSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminals = project.Wires.SelectMany(w => new[] { w.From, w.To })
            .Where(t => t.Part == board.Id).Select(t => t.Pin).ToHashSet();
        var wired = Enumerable.Range(0, Esp32S3Board.GpioCount)
            .Where(i => Esp32S3Board.Gpio(i) is { } pin && terminals.Contains(pin)).ToArray();
        var circuit = CircuitSolver.Solve(project);
        var initial = Esp32S3Board.Inputs(board, circuit, wired, null);
        var initialAnalog = Esp32S3Board.AnalogInputs(board, circuit, wired);
        if (project.Parts.Any(CircuitI2c.IsDevice)) s3I2c = new Esp32S3I2cWorker();
        var execution = Esp32S3GpioRunner.Run(image, runtimePath, async (session, lifetime) =>
        {
            await session.ExchangePads(initial, lifetime);
            connected.TrySetResult(session);
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime);
        }, cancellation, initialAnalog, displayBus: s3I2c is not null);

        s3Execution = execution;
        await Task.WhenAny(connected.Task, execution).WaitAsync(cancellation);
        if (execution.IsCompleted) await execution;
        if (generation != runId || disposed) return;
        s3Session = await connected.Task;
        s3WiredPins = wired;
        serialReady = s3Session.SupportsSerial;
        ClearSerialDisplay();
        UpdateSerialControls();
    }

    async Task<(PinDrive[] Pins, Esp32S3SerialFrame? Serial, Esp32S3I2cFrame? I2c)> StepEsp32S3(
        CircuitPart board, ElectronicsSerialIo? serial, CancellationToken cancellation)
    {
        if (s3Execution is { IsCompleted: true }) await s3Execution;
        var session = s3Session ?? throw new IOException("Связь с эмулятором ESP32-S3 завершена.");
        var before = CircuitSolver.Solve(project, outputs);
        var inputs = Esp32S3Board.Inputs(board, before, s3WiredPins, outputs);
        var analog = session.SupportsAnalog ? Esp32S3Board.AnalogInputs(board, before, s3WiredPins) : null;
        var pins = Esp32S3Board.Drives(await session.ExchangePads(inputs, cancellation, analog));
        var i2c = s3I2c is { } worker
            ? await session.ExchangeI2c(worker, Esp32S3Board.I2cDevices(project, board, before), cancellation)
            : null;
        var frame = session.SupportsSerial ? await session.ExchangeSerial(serial, cancellation) : null;
        return (pins, frame, i2c);
    }

    bool StopEsp32S3()
    {
        var execution = s3Execution;
        s3Execution = null;
        s3Session = null;
        s3WiredPins = [];
        s3I2c?.Dispose();
        s3I2c = null;
        if (execution is null) return false;
        // A normal stop cancels the job. Observe faults even if this screen is
        // closed before another tick can see the end of the process.
        _ = execution.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }
}
