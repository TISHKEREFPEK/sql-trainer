using Classroom.Desktop.Electronics;
using Classroom.Desktop;
using Classroom.Client;
using Classroom.Contracts;

if (args.Length == 2 && args[0] == "sql-store")
{
    var input = Wire.Read<SqlStoreInput>(File.ReadAllText(args[1]));
    using var store = new SqlLocalStore(input.Root, input.Fingerprint, input.ProfileId);
    store.Observe(input.LessonId, input.Task);
    store.Edit(input.LessonId, input.Task.WorkScope, input.Task.Id, input.Request.Code, 4);
    store.Action(input.LessonId, input.Task.WorkScope, input.Task.Id, "opened");
    store.Remember(new PendingExecution(input.Task.Id, input.Request));
    await File.WriteAllTextAsync(args[1] + ".ready", "ready");
    await Task.Delay(Timeout.InfiniteTimeSpan); // Parent kills this test host without Dispose.
    return 5;
}

if (args.Length == 2 && args[0] == "electronics-history")
{
    var input = Wire.Read<ElectronicsHistoryInput>(File.ReadAllText(args[1]));
    using var store = new ElectronicsHistoryStore(input.Root, input.Context, input.InitialJson);
    store.Record(input.EditedJson, "code-edit");
    store.Record(input.EditedJson, "report-submitted", true, reportId: input.ReportId, reportHash: SqlSyncHash.Text(input.EditedJson));
    await File.WriteAllTextAsync(args[1] + ".ready", "ready");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 6;
}

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

sealed record SqlStoreInput(string Root, string Fingerprint, string ProfileId, string LessonId, StudentTask Task, ExecuteRequest Request);
sealed record ElectronicsHistoryInput(string Root, ElectronicsHistoryContext Context, string InitialJson, string EditedJson, string ReportId);
