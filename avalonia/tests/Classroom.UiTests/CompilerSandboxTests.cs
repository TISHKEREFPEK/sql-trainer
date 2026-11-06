using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class CompilerSandboxTests
{
    [Fact]
    public async Task AbruptHostExitKillsJobAndNextStartRecoversOnlyOwnedResources()
    {
        Assert.True(OperatingSystem.IsWindows()); if (!OperatingSystem.IsWindows()) return;
        var (runtime, scratch) = NodeFolders(); var leases = Path.Combine(Path.GetDirectoryName(scratch)!, "host-leases");
        var probe = Environment.GetEnvironmentVariable("CLASSROOM_TEST_SANDBOX_PROBE") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Classroom.SandboxProbe", "release", "Classroom.SandboxProbe.dll"));
        Assert.True(File.Exists(probe), "Build Classroom.SandboxProbe and set CLASSROOM_TEST_SANDBOX_PROBE when using default output paths.");
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("CLASSROOM_DOTNET") ?? "dotnet") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(probe); start.ArgumentList.Add(runtime); start.ArgumentList.Add(scratch); start.Environment["CLASSROOM_COMPILER_LEASES"] = leases;
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(true); }
        Assert.True(process.ExitCode == 73, await output + await error);
        var recordFile = Assert.Single(Directory.GetFiles(leases, "*.json"));
        var record = System.Text.Json.JsonSerializer.Deserialize<CompilerLease.Record>(File.ReadAllText(recordFile))!;
        Assert.Equal(2, record.Drives.Count);
        var pid = int.Parse(File.ReadAllText(Path.Combine(scratch, "probe.ready")), System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < 100; i++)
        {
            try { using var child = System.Diagnostics.Process.GetProcessById(pid); if (child.HasExited) break; }
            catch (ArgumentException) { break; }
            await Task.Delay(25);
        }
        CompilerLease.RecoverOrphans(leases);
        Assert.Empty(Directory.GetFiles(leases, "*.json"));
        foreach (var drive in record.Drives) Assert.False(Directory.Exists(drive.Device + "\\"));
        try { using var child = System.Diagnostics.Process.GetProcessById(pid); Assert.True(child.HasExited); } catch (ArgumentException) { }
    }
    static (string Runtime, string Scratch) NodeFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "classroom-sandbox-probe-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime"); var scratch = Path.Combine(root, "scratch");
        Directory.CreateDirectory(runtime); Directory.CreateDirectory(scratch);
        var source = Environment.GetEnvironmentVariable("CLASSROOM_ELECTRONICS_RUNTIME") ?? throw new IOException("Set test runtime.");
        File.Copy(Path.Combine(source, "node.exe"), Path.Combine(runtime, "node.exe")); return (runtime, scratch);
    }
    [Fact]
    public async Task CancellationTerminatesDescendantsAndReleasesTheirResources()
    {
        Assert.True(OperatingSystem.IsWindows()); if (!OperatingSystem.IsWindows()) return;
        var (runtime, scratch) = NodeFolders(); WindowsSandboxProcess.GrantRuntimeRead(runtime);
        var pidFile = Path.Combine(scratch, "child.pid");
        var child = "require('node:fs').writeFileSync(process.argv[1],String(process.pid));setInterval(()=>{},1000);";
        var script = "require('node:child_process').spawn(process.execPath,['-e',process.argv[2],'--',process.argv[1]],{stdio:'inherit'});setInterval(()=>{},1000);";
        using var cancellation = new CancellationTokenSource();
        var running = WindowsSandboxProcess.Run(Path.Combine(runtime, "node.exe"), ["-e", script, "--", pidFile, child], runtime, scratch, new Dictionary<string, string>(), cancellation.Token);
        for (var i = 0; i < 100 && !File.Exists(pidFile); i++) await Task.Delay(50);
        Assert.True(File.Exists(pidFile)); var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        cancellation.Cancel(); await Assert.ThrowsAsync<IOException>(() => running);
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }
    [Fact]
    public async Task NoNetworkCapabilityPreventsLoopbackConnection()
    {
        Assert.True(OperatingSystem.IsWindows()); if (!OperatingSystem.IsWindows()) return;
        var (runtime, scratch) = NodeFolders(); WindowsSandboxProcess.GrantRuntimeRead(runtime);
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using (var control = new System.Net.Sockets.TcpClient()) await control.ConnectAsync(System.Net.IPAddress.Loopback, port);
        var script = "const s=require('node:net').connect({host:'127.0.0.1',port:+process.argv[1]});s.on('connect',()=>{console.log('allowed');s.destroy()});s.on('error',e=>console.log('denied:'+e.code));s.setTimeout(1500,()=>{console.log('timeout');s.destroy()});";
        var result = await WindowsSandboxProcess.Run(Path.Combine(runtime, "node.exe"), ["-e", script, "--", port.ToString(System.Globalization.CultureInfo.InvariantCulture)], runtime, scratch, new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.True(new[] { "denied:EACCES", "denied:EPERM", "denied:ETIMEDOUT" }.Any(result.Output.Contains), result.Output);
        using var stillReachable = new System.Net.Sockets.TcpClient(); await stillReachable.ConnectAsync(System.Net.IPAddress.Loopback, port);
    }
    [Fact]
    public async Task CompilerCannotIncludeOrEmbedPrivateFilesAndKeepsArduinoPrototypes()
    {
        var root = Path.Combine(Path.GetTempPath(), "classroom-compiler-private-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var header = Path.Combine(root, "private.h"); var binary = Path.Combine(root, "private.bin");
        File.WriteAllText(header, "#error SYNTHETIC_PRIVATE_CONTENT_MUST_NOT_BE_READ"); File.WriteAllBytes(binary, [1, 2, 3, 4]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => FirmwareRunner.Compile("#include \"" + header.Replace('\\', '/') + "\"\nvoid setup(){} void loop(){}", CancellationToken.None));
        Assert.DoesNotContain("SYNTHETIC_PRIVATE_CONTENT", error.Message); Assert.Contains("Permission denied", error.Message);
        var code = $$"""
            asm(".incbin \"{{binary.Replace('\\', '/')}}\"\n");
            void setup(){} void loop(){}
            """;
        var embedded = await Assert.ThrowsAsync<InvalidDataException>(() => FirmwareRunner.Compile(code, CancellationToken.None));
        Assert.True(embedded.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || embedded.Message.Contains("file not found", StringComparison.OrdinalIgnoreCase), embedded.Message);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(binary));
        Assert.StartsWith(":", await FirmwareRunner.Compile("void setup(){laterDefined(1);} void loop(){} int laterDefined(int value){return value+1;}", CancellationToken.None));
    }
    [Fact]
    public async Task PreparedAvrDriverRunsInsideContainer()
    {
        Assert.True(OperatingSystem.IsWindows()); if (!OperatingSystem.IsWindows()) return;
        var source = Environment.GetEnvironmentVariable("CLASSROOM_ELECTRONICS_RUNTIME") ?? throw new IOException("Set test runtime.");
        var runtime = await CompilerRuntime.Prepare(source, CancellationToken.None);
        var scratch = Path.Combine(Path.GetTempPath(), "classroom-firmware-driver-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        var executable = Path.Combine(runtime, "sandbox-avr", "bin", "avr-g++.exe");
        var result = await WindowsSandboxProcess.Run(executable, ["--version"], runtime, scratch, new Dictionary<string, string>(), CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"Driver exit {result.ExitCode}: {result.Error}"); Assert.Contains("16.1.0", result.Output);
    }
    [Fact]
    public async Task ProcessAndChildCanUseScratchButCannotReadPrivateFilesOrChangeRuntime()
    {
        Assert.True(OperatingSystem.IsWindows());
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "classroom-sandbox-probe-" + Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime"); var scratch = Path.Combine(root, "scratch"); var outside = Path.Combine(root, "private");
        Directory.CreateDirectory(runtime); Directory.CreateDirectory(scratch); Directory.CreateDirectory(outside);
        var source = Environment.GetEnvironmentVariable("CLASSROOM_ELECTRONICS_RUNTIME") ?? throw new IOException("Set test runtime.");
        File.Copy(Path.Combine(source, "node.exe"), Path.Combine(runtime, "node.exe"));
        var secret = Path.Combine(outside, "private.txt"); File.WriteAllText(secret, "synthetic private marker");
        var probe = Path.Combine(runtime, "probe.cjs");
        File.WriteAllText(probe, """
            const fs=require('node:fs'),cp=require('node:child_process');
            const [secret,scratch,runtime,child,script]=process.argv.slice(1);
            console.error('probe-start',child||'parent');
            function denied(action){try{action();return false}catch{return true}}
            const result={readDenied:denied(()=>fs.readFileSync(secret)),writeDenied:denied(()=>fs.writeFileSync(secret,'bad')),
              runtimeDenied:denied(()=>fs.writeFileSync(require('node:path').join(runtime,'forbidden.txt'),'bad')),
              cleanEnvironment:!process.env.CLASSROOM_DATA&&!process.env.OPENAI_API_KEY};
            fs.writeFileSync(require('node:path').join(scratch,child?'child.txt':'allowed.txt'),'ok');
            console.error('probe-files-complete',child||'parent');
            result.level=child||'parent';
            if(!child){const r=cp.spawnSync(process.execPath,['-e',script,'--',secret,scratch,runtime,'child',script],{stdio:'inherit'});result.childExit=r.status;}
            console.log(JSON.stringify(result));
            """);
        WindowsSandboxProcess.GrantRuntimeRead(runtime);
        var script = File.ReadAllText(probe);
        var result = await WindowsSandboxProcess.Run(Path.Combine(runtime, "node.exe"), ["-e", script, "--", secret, scratch, runtime, "", script], runtime, scratch, new Dictionary<string, string>(), CancellationToken.None);
        Assert.True(result.ExitCode == 0, $"Sandbox exit {result.ExitCode}: {result.Error}");
        var rows = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => System.Text.Json.JsonDocument.Parse(line)).ToArray();
        Assert.Equal(2, rows.Length);
        foreach (var row in rows) foreach (var name in new[] { "readDenied", "writeDenied", "runtimeDenied", "cleanEnvironment" }) Assert.True(row.RootElement.GetProperty(name).GetBoolean(), row.RootElement.GetProperty("level").GetString() + " " + name);
        Assert.Equal(0, rows.Single(r => r.RootElement.GetProperty("level").GetString() == "parent").RootElement.GetProperty("childExit").GetInt32());
        Assert.True(File.Exists(Path.Combine(scratch, "allowed.txt"))); Assert.True(File.Exists(Path.Combine(scratch, "child.txt")));
        Assert.Equal("synthetic private marker", File.ReadAllText(secret)); Assert.False(File.Exists(Path.Combine(runtime, "forbidden.txt")));
    }
}
