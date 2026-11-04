using Classroom.Desktop.Electronics;

if (!OperatingSystem.IsWindows() || args.Length != 2) return 2;
var runtime = Path.GetFullPath(args[0]); var scratch = Path.GetFullPath(args[1]);
WindowsSandboxProcess.GrantRuntimeRead(runtime);
var ready = Path.Combine(scratch, "probe.ready");
var running = WindowsSandboxProcess.Run(Path.Combine(runtime, "node.exe"),
    ["-e", "require('node:fs').writeFileSync(process.argv[1],String(process.pid));setInterval(()=>{},1000);", "--", ready], runtime, scratch, new Dictionary<string, string>(), CancellationToken.None);
for (var i = 0; i < 200 && !File.Exists(ready); i++)
{
    if (running.IsCompleted) { await running; return 3; }
    await Task.Delay(25);
}
if (!File.Exists(ready)) return 4;
// Intentionally bypass finally/Dispose, like an abruptly terminated host.
Environment.Exit(73);
return 0;
