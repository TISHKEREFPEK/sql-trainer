using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class Esp32S3GpioFactAttribute : FactAttribute
{
    public Esp32S3GpioFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLASSROOM_ESP32S3_GPIO_RUNTIME")))
            Skip = "Requires the optional QEMU build with ESP32-S3 GPIO.";
    }
}

public sealed class Esp32S3GpioRunnerTests
{
    const int Button = 12;
    const int Led = 39;

    [Esp32S3GpioFact]
    public async Task IsolatedArduinoFollowsHostInputsAndReleasesDisconnectedPads()
    {
        var runtime = Environment.GetEnvironmentVariable("CLASSROOM_ESP32S3_GPIO_RUNTIME")!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "esp32s3-gpio.ino"), deadline.Token);
        var image = await Esp32S3Compiler.CompileArduino(source, deadline.Token);
        await Esp32S3GpioRunner.Run(image, runtime, async (session, cancellation) =>
        {
            Dictionary<int, bool?> inputs = new() { [Button] = null, [Led] = null };
            var initial = await WaitForLevel(session, inputs, true, cancellation);
            Assert.True(initial[Button].PullUp);
            Assert.True(initial[Button].InputHigh);
            Assert.False(initial[Button].Driven);

            inputs[Button] = false;
            var pressed = await WaitForLevel(session, inputs, false, cancellation);
            Assert.False(pressed[Button].InputHigh);

            inputs.Remove(Button);
            await WaitForLevel(session, inputs, true, cancellation);
            inputs[Button] = null;
            var released = await WaitForLevel(session, inputs, true, cancellation);
            Assert.True(released[Button].InputHigh);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => session.ExchangePads(new Dictionary<int, bool?> { [22] = true }, cancellation));
        }, deadline.Token);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Esp32S3GpioRunner.Run(image, runtime,
            async (session, cancellation) =>
            {
                await WaitForLevel(session, new Dictionary<int, bool?> { [Led] = null }, true, cancellation);
                stop.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }, stop.Token));
    }

    static async Task<IReadOnlyDictionary<int, Esp32S3PadState>> WaitForLevel(Esp32S3GpioSession session,
        IReadOnlyDictionary<int, bool?> inputs, bool high, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            var frame = await session.ExchangePads(inputs, timeout.Token);
            if (frame[Led].Driven && frame[Led].OutputHigh == high) return frame;
            await Task.Delay(20, timeout.Token);
        }
    }
}
