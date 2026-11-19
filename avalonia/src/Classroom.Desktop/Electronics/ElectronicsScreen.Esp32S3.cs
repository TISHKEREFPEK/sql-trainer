using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

public sealed partial class ElectronicsScreen
{
    Task? s3Execution;
    Esp32S3GpioSession? s3Session;
    int[] s3WiredPins = [];

    async Task StartEsp32S3(CircuitPart board, Esp32S3SketchImage image, string runtimePath, long generation, CancellationToken cancellation)
    {
        var connected = new TaskCompletionSource<Esp32S3GpioSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminals = project.Wires.SelectMany(w => new[] { w.From, w.To })
            .Where(t => t.Part == board.Id).Select(t => t.Pin).ToHashSet();
        var wired = Enumerable.Range(0, Esp32S3Board.GpioCount)
            .Where(i => Esp32S3Board.Gpio(i) is { } pin && terminals.Contains(pin)).ToArray();
        var initial = Esp32S3Board.Inputs(board, CircuitSolver.Solve(project), wired, null);
        var execution = Esp32S3GpioRunner.Run(image, runtimePath, async (session, lifetime) =>
        {
            await session.ExchangePads(initial, lifetime);
            connected.TrySetResult(session);
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime);
        }, cancellation);

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

    async Task<(PinDrive[] Pins, Esp32S3SerialFrame? Serial)> StepEsp32S3(
        CircuitPart board, ElectronicsSerialIo? serial, CancellationToken cancellation)
    {
        if (s3Execution is { IsCompleted: true }) await s3Execution;
        var session = s3Session ?? throw new IOException("Связь с эмулятором ESP32-S3 завершена.");
        var before = CircuitSolver.Solve(project, outputs);
        var inputs = Esp32S3Board.Inputs(board, before, s3WiredPins, outputs);
        var pins = Esp32S3Board.Drives(await session.ExchangePads(inputs, cancellation));
        var frame = session.SupportsSerial ? await session.ExchangeSerial(serial, cancellation) : null;
        return (pins, frame);
    }

    bool StopEsp32S3()
    {
        var execution = s3Execution;
        s3Execution = null;
        s3Session = null;
        s3WiredPins = [];
        if (execution is null) return false;
        // A normal stop cancels the job. Observe faults even if this screen is
        // closed before another tick can see the end of the process.
        _ = execution.ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }
}
