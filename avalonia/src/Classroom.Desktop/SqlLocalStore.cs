using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop;

public sealed record SqlConflictArchive(long At, string LocalCode, string ServerCode, string Choice);
public sealed record SqlRejectedExecution(long At, ExecuteRequest Request, string Error);
public sealed record SqlLocalWork(string LessonId, StudentTask Task, string Code, string BaseHash, bool Dirty = false,
    SqlDraftSync? PendingDraft = null, SqlDraftReceipt? Conflict = null, ExecuteRequest? Execution = null,
    SqlActionSync[]? Actions = null, SqlConflictArchive[]? Resolutions = null, string? Error = null, int Caret = 0,
    ExecutionDto? LastExecution = null, SqlRejectedExecution[]? RejectedExecutions = null, SqlTextJournal? Journal = null, SqlJournalIndex? JournalIndex = null,
    SqlJournalDeliveryState? JournalDelivery = null);

// This store contains learning data only. It never serializes a ClassroomApi,
// login/password/token, teacher seed, solution or unopened hint.
public sealed partial class SqlLocalStore : IDisposable
{
    sealed record Envelope(int Version, string ProfileId, string Fingerprint, string Payload, string Hash);
    readonly string directory, profileId, fingerprint;
    readonly FileStream lease;
    readonly object gate = new();
    readonly SemaphoreSlim delivery = new(1, 1);
    readonly Dictionary<string, SqlLocalWork> works = new();
    bool disposed;
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string Key(string lesson, string scope, string task) => SqlSyncHash.Text(Wire.Write(new[] { lesson, scope, task }));
    public SqlLocalStore(string root, string fingerprint, string profileId)
    {
        this.profileId = profileId; this.fingerprint = fingerprint;
        directory = Path.Combine(Path.GetFullPath(root), "sql", SqlSyncHash.Text(fingerprint + ":" + profileId));
        Directory.CreateDirectory(directory);
        try { lease = new FileStream(Path.Combine(directory, "open.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new IOException("Локальные SQL-работы уже открыты в другом окне или недоступны для записи.", e); }
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.work.json"))
            {
                SqlLocalWork work;
                try { work = ReadWork(file, file, profileId, fingerprint); }
                catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException)
                { throw new SqlLocalRecoveryException(InspectRecovery(file, profileId, fingerprint, e.Message)); }
                works.Add(Key(work.LessonId, work.Task.WorkScope, work.Task.Id), work);
            }
        }
        catch { lease.Dispose(); throw; }
    }
    void Save(SqlLocalWork work)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var key = Key(work.LessonId, work.Task.WorkScope, work.Task.Id);
        var stored = StorePages(work, directory, pageCache);
        var payload = Wire.Write(stored); var json = Wire.Write(new Envelope(stored.JournalIndex is not null ? 3 : 1, profileId, fingerprint, payload, SqlSyncHash.Text(payload)));
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 64 * 1024 * 1024) throw new IOException("Локальная очередь SQL заполнена. Новое изменение не записано на диск; восстановите связь или сохраните код отдельно.");
        var file = Path.Combine(directory, key + ".work.json"); var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json); stream.Write(bytes); stream.Flush(true);
            }
            if (File.Exists(file)) FileOperation(() => File.Copy(file, file + ".backup", true));
            FileOperation(() => File.Move(temporary, file, true));
            works[key] = work;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static void FileOperation(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && (e.HResult & 0xffff) is 5 or 32 or 33 && attempt < 5)
            { Thread.Sleep(10 * (attempt + 1)); }
        }
    }
    public SqlLocalWork? Find(string lesson, string scope, string task)
    { lock (gate) return works.GetValueOrDefault(Key(lesson, scope, task)); }
    public SqlLocalWork Observe(string lesson, StudentTask task)
    {
        lock (gate)
        {
            var previous = Find(lesson, task.WorkScope, task.Id);
            if (previous is not null && (previous.Task.Revision != task.Revision || previous.Task.VariantIndex != task.VariantIndex))
                throw new InvalidDataException("На ПК сохранена другая редакция задания. Локальная работа оставлена без изменений.");
            var next = previous is null ? new SqlLocalWork(lesson, task, task.Draft.Length > 0 ? task.Draft : task.Starter, SqlSyncHash.Text(task.Draft))
                : previous.Dirty || previous.PendingDraft is not null || previous.Conflict is not null ? previous with { Task = task }
                : previous with { Task = task, Code = task.Draft.Length > 0 ? task.Draft : task.Starter, BaseHash = SqlSyncHash.Text(task.Draft) };
            next = next with { Journal = SqlTextJournal.Record(previous?.Journal, previous?.Code ?? next.Code, next.Code, "server", "observe", Now) };
            Save(next); return next;
        }
    }
    public void Edit(string lesson, string scope, string task, string code, int caret)
    {
        if (code.Length > 12000) throw new InvalidDataException("SQL слишком длинный: до 12000 символов. Новое изменение не записано на диск.");
        lock (gate)
        {
            var work = Find(lesson, scope, task) ?? throw new InvalidDataException("Не найден локальный контекст SQL.");
            Save(work with { Code = code, Caret = Math.Clamp(caret, 0, code.Length), Dirty = true, Error = null,
                Journal = SqlTextJournal.Record(work.Journal, work.Code, code, "student", "edit", Now) });
        }
    }
    public PendingExecution? Pending(string lesson, string scope, string task)
    { var request = Find(lesson, scope, task)?.Execution; return request is null ? null : new(task, request); }
    public void Remember(PendingExecution execution)
    {
        lock (gate)
        {
            var request = execution.Request; var work = Find(request.LessonId!, request.WorkScope!, execution.TaskId) ?? throw new InvalidDataException("Не найден локальный контекст запроса.");
            Save(work with { Execution = request });
        }
    }
    public void Acknowledge(PendingExecution execution, ExecutionDto result)
    {
        lock (gate)
        {
            var request = execution.Request; var work = Find(request.LessonId!, request.WorkScope!, execution.TaskId)!;
            if (work.Execution?.OperationId == request.OperationId) Save(work with { Execution = null, LastExecution = result });
        }
    }
    public void Action(string lesson, string scope, string task, string action, int seconds = 0)
    {
        lock (gate)
        {
            var work = Find(lesson, scope, task) ?? throw new InvalidDataException("Не найден локальный контекст действия.");
            Save(work with { Actions = (work.Actions ?? []).Append(new(Guid.NewGuid().ToString("N"), task, lesson, scope, action, Now, seconds)).ToArray() });
        }
    }
    public void Reject(PendingExecution execution, string error)
    {
        lock (gate)
        {
            var request = execution.Request; var work = Find(request.LessonId!, request.WorkScope!, execution.TaskId)!;
            if (work.Execution?.OperationId == request.OperationId)
                Save(work with { Execution = null, RejectedExecutions = (work.RejectedExecutions ?? []).Append(new(Now, request, error)).ToArray() });
        }
    }
    public void Resolve(string lesson, string scope, string task, bool keepLocal)
    {
        lock (gate)
        {
            var work = Find(lesson, scope, task)!; var conflict = work.Conflict ?? throw new InvalidDataException("Конфликт уже разрешён.");
            var chosen = keepLocal ? work.Code : conflict.ServerCode;
            Save(work with { BaseHash = conflict.ServerHash, Code = chosen,
                Dirty = true, PendingDraft = null, Conflict = null, Error = null,
                Journal = SqlTextJournal.Record(work.Journal, work.Code, chosen, "student", keepLocal ? "conflict-local" : "conflict-server", Now, true),
                Resolutions = (work.Resolutions ?? []).Append(new(Now, work.Code, conflict.ServerCode, keepLocal ? "local" : "server")).ToArray() });
        }
    }
    void Bound(ClassroomApi api)
    {
        if (api.ProfileId != profileId || api.Connection.Fingerprint != fingerprint) throw new InvalidDataException("Локальная очередь относится к другому ученику или серверу.");
    }
    public async Task Drain(ClassroomApi api, CancellationToken cancellation = default)
    {
        await delivery.WaitAsync(cancellation);
        try
        {
            SqlLocalWork[] snapshot; lock (gate) { if (disposed) return; snapshot = works.Values.ToArray(); }
            Bound(api);
            foreach (var item in snapshot)
            {
                try
                {
                    Bound(api);
                    SqlLocalWork Current() => Find(item.LessonId, item.Task.WorkScope, item.Task.Id)!;
                    foreach (var action in Current().Actions ?? [])
                    {
                        Bound(api); await api.Post<System.Text.Json.JsonElement>("sql-sync/action", action, cancellation);
                        lock (gate) { if (disposed) return; var next = Current(); Save(next with { Actions = (next.Actions ?? []).Where(a => a.Id != action.Id).ToArray(), Error = null }); }
                    }
                    if (Current().Execution is { } pending)
                    {
                        Bound(api); var receipt = await api.Post<SqlExecutionReceipt>("sql-sync/receipt", new SqlExecutionReceiptRequest(item.Task.Id, pending), cancellation);
                        if (receipt.Found && receipt.Result is not null) { lock (gate) { if (disposed) return; Acknowledge(new(item.Task.Id, pending), receipt.Result); } }
                    }
                    await DeliverJournal(api, item, cancellation);
                    SqlDraftSync? draft;
                    lock (gate)
                    {
                        if (disposed) return; var next = Current(); draft = null;
                        if (next.Dirty && next.Conflict is null)
                        {
                            draft = next.PendingDraft ?? new(Guid.NewGuid().ToString("N"), next.Task.Id, next.LessonId, next.Task.WorkScope, next.BaseHash, next.Code, Now);
                            if (next.PendingDraft is null) Save(next with { PendingDraft = draft });
                        }
                    }
                    if (draft is null) continue;
                    Bound(api); var reply = await api.Put<SqlDraftReceipt>("sql-sync/draft", draft, cancellation);
                    if (reply.Id != draft.Id || reply.ServerHash != SqlSyncHash.Text(reply.ServerCode) || reply.Status is not ("saved" or "conflict"))
                        throw new InvalidDataException("Сервер вернул некорректное подтверждение SQL.");
                    lock (gate)
                    {
                        if (disposed) return; var next = Current();
                        if (next.PendingDraft?.Id != draft.Id) continue;
                        Save(reply.Status == "conflict" ? next with { Conflict = reply, Error = null }
                            : next with { BaseHash = reply.ServerHash, Dirty = next.Code != draft.Code, PendingDraft = null, Error = null, Task = next.Task with { Draft = reply.ServerCode } });
                    }
                }
                catch (ApiException e) when (e.Status is 400 or 403 or 404 or 409)
                { lock (gate) { if (disposed) return; var next = Find(item.LessonId, item.Task.WorkScope, item.Task.Id)!; Save(next with { Error = e.Message }); } }
            }
        }
        finally { delivery.Release(); }
    }
    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; lease.Dispose(); } }
}
