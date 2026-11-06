using Classroom.Client;
using Classroom.Contracts;
using Avalonia.Threading;
using System.Text.Json.Serialization;
namespace Classroom.Desktop.Electronics;

public sealed class ElectronicsSync : IDisposable
{
    public sealed class Outbox
    {
        [JsonRequired] public string ClientId { get; set; }=Guid.NewGuid().ToString("N");
        [JsonRequired] public long Sequence { get; set; }
        [JsonRequired] public string ProjectJson { get; set; }="";
        public bool Dirty { get; set; }
        [JsonRequired] public List<ElectronicsReportRequest> Reports { get; set; }=[];
        public string? AssignmentId { get; set; }
    }
    readonly ClassroomApi api;
    readonly string path;
    readonly Action<string> status;
    readonly Action<ElectronicsSyncReceipt>? receipt;
    readonly Func<CancellationToken, Task<bool>>? journalDelivery;
    readonly Func<bool>? journalPending;
    volatile bool historyBusy;
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(2)};
    readonly CancellationTokenSource lifetime=new();
    readonly object stateGate=new();
    Outbox state=new();
    volatile bool busy,disposed,lastFailure;
    bool durable;
    internal string? DeliveryError {get;private set;}
    bool DraftPending {get{lock(stateGate)return state.Dirty||state.Reports.Count>0;}}
    bool StoragePending {get{lock(stateGate)return !durable;}}
    public bool Pending => StoragePending || DraftPending || journalPending?.Invoke() == true;
    public string ClientId {get{lock(stateGate)return state.ClientId;}}
    public bool PendingSubmission {get{lock(stateGate)return state.Reports.Any(r=>r.Kind=="submitted");}}
    public bool HasStaleSubmission(int currentVersion)
    {
        lock(stateGate) return state.Reports.Any(r => r.Kind == "submitted" && (r.ExpectedAssignmentVersion ?? 1) != currentVersion);
    }
    // Called only after the pupil explicitly chooses to create a new submission.
    // Preserve the unacknowledged intent separately; never rewrite its UUID/version.
    public void ArchiveStaleSubmissions(int currentVersion)
    {
        lock(stateGate)
        {
            var stale = state.Reports.Where(r => r.Kind == "submitted" && (r.ExpectedAssignmentVersion ?? 1) != currentVersion).ToArray();
            if (stale.Length == 0) return;
            File.WriteAllText(path + ".superseded-" + Guid.NewGuid().ToString("N") + ".json", Wire.Write(new { state.ClientId, state.AssignmentId, Reports = stale }));
            state.Reports.RemoveAll(r => stale.Any(s => s.Id == r.Id)); Persist();
        }
    }
    public ElectronicsSync(ClassroomApi api,string path,Action<string> status,bool startTimer=true,string? assignmentId=null,Action<ElectronicsSyncReceipt>? receipt=null,
        Func<CancellationToken, Task<bool>>? journalDelivery=null,Func<bool>? journalPending=null,bool requireExisting=false,string? expectedClientId=null)
    {
        this.api=api; this.path=path; this.status=status; this.receipt=receipt; this.journalDelivery=journalDelivery; this.journalPending=journalPending;
        if(File.Exists(path)) { state=ReadOutbox(path,assignmentId,expectedClientId); durable=true; }
        else if(requireExisting || expectedClientId is not null) throw new InvalidDataException("Отсутствует исходная очередь схемы. История сохранена; требуется восстановление привязки.");
        state.AssignmentId=assignmentId;
        // Re-send the local draft once after login, including after server restore.
        state.Dirty=true;
        timer.Tick+=async(_,_)=>await Flush();
        if(startTimer) Start();
    }
    internal void Start() { ObjectDisposedException.ThrowIf(disposed,this); timer.Start(); }
    internal static Outbox ReadOutbox(string path,string? assignmentId,string? expectedClientId=null)
    {
        if(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Очередь через файловую ссылку не поддерживается.");
        if(new FileInfo(path).Length>64_000_000) throw new InvalidDataException("Слишком большая очередь.");
        var value=Wire.Read<Outbox>(File.ReadAllText(path));
        if(value.AssignmentId!=assignmentId) throw new InvalidDataException("Очередь относится к другой работе. Исходный файл сохранён.");
        if(!Guid.TryParseExact(value.ClientId,"N",out _)||value.Sequence<1||value.Reports is null||value.Reports.Count>100||string.IsNullOrEmpty(value.ProjectJson)) throw new InvalidDataException("Некорректная очередь результатов.");
        if(expectedClientId is not null && value.ClientId!=expectedClientId) throw new InvalidDataException("Очередь не соответствует исходной ClientId-привязке истории.");
        CircuitProject.Parse(value.ProjectJson);
        var ids=new HashSet<string>();
        foreach(var report in value.Reports)
        {
            if(report is null || report.ProjectJson is null || report.Detail is null || !Guid.TryParseExact(report.Id,"N",out _) || !ids.Add(report.Id) ||
                !ElectronicsProjectFormat.ReportKinds.Contains(report.Kind)||report.Detail.Length>16000||report.ExpectedAssignmentVersion is < 1) throw new InvalidDataException("Некорректный отчёт.");
            CircuitProject.Parse(report.ProjectJson);
        }
        return value;
    }
    public void QueueDraft(string json)
    {
        lock(stateGate)
        {
        if(disposed) return;
        if(state.ProjectJson!=json) { state.ProjectJson=json; state.Sequence=checked(state.Sequence+1); state.Dirty=true; }
        if(state.Sequence==0) state.Sequence=1;
        Persist(); if(Pending) status("Ожидает передачи преподавателю…");
        }
    }
    public void Record(string kind,string json,string detail="",int? expectedAssignmentVersion=null,string? reportId=null)
    {
        lock(stateGate)
        {
        if(disposed) return;
        if(!ElectronicsProjectFormat.ReportKinds.Contains(kind)) throw new InvalidDataException("Неизвестный вид результата.");
        if(reportId is not null && !Guid.TryParseExact(reportId,"N",out _)) throw new InvalidDataException("Некорректный номер результата.");
        if(state.AssignmentId is not null && kind=="submitted" && state.Reports.Any(r=>r.Kind=="submitted")) return;
        if(state.Reports.Count>=100 || state.Reports.Sum(r=>r.ProjectJson.Length)+(long)json.Length>16_000_000) throw new InvalidDataException("Очередь результатов заполнена. Подключитесь к преподавателю и повторите отправку; локальная схема сохранена.");
        var explanation = detail.Length>16000 ? detail[..15900]+"\n[Сообщение сокращено. Полный текст сохранён в приложении ученика.]" : detail;
        state.Reports.Add(new(reportId??Guid.NewGuid().ToString("N"),kind,json,explanation,expectedAssignmentVersion)); Persist(); status("Результат сохранён локально и ожидает передачи.");
        }
    }
    async Task<bool> FlushDraft(CancellationToken cancellation)
    {
        ElectronicsReportRequest[] reports;long sequence;string json,client;string? assignmentId;
        lock(stateGate)
        {
            if(!durable && !disposed && !busy && state.ProjectJson.Length>0)
            {
                try { Persist(); }
                catch(Exception e) when(e is IOException or InvalidDataException or UnauthorizedAccessException)
                { lastFailure=true; DeliveryError="Очередь передачи не записана на ПК: " + e.Message; status(DeliveryError); return false; }
            }
            if(disposed||busy||!DraftPending||state.ProjectJson.Length==0) return !DraftPending;
            busy=true;reports=state.Reports.Take(10).ToArray();sequence=state.Sequence;json=state.ProjectJson;client=state.ClientId;assignmentId=state.AssignmentId;
        }
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var ack=await api.Send<ElectronicsSyncReceipt>(HttpMethod.Put,"electronics/work",new ElectronicsSyncRequest(client,sequence,json,reports,assignmentId),timeout.Token);
            lock(stateGate)
            {
            if(disposed) return false;
            lastFailure=false;
            if(state.Sequence==sequence && state.ProjectJson==json) state.Dirty=false;
            var ids=ack.ReportIds.ToHashSet(); state.Reports.RemoveAll(r=>ids.Contains(r.Id)); Persist();
            receipt?.Invoke(ack);
            status(DraftPending?"Часть результатов передана; очередь отправляется…":journalPending?.Invoke()==true?"Снимок и результаты переданы; история ожидает отправки.":"Схема и результаты сохранены у преподавателя."); return !DraftPending;
            }
        }
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException or ApiException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            lastFailure=true;
            DeliveryError=e is ApiException a ? a.Status==401 ? "Войдите снова для передачи результатов. Локальная схема сохранена." : a.Message
                : e is IOException or InvalidDataException or UnauthorizedAccessException ? "Не удалось сохранить подтверждение передачи на ПК: " + e.Message
                : "Нет связи: работа и результаты сохранены локально, отправка повторится.";
            if(!disposed) status(DeliveryError);
            return false;
        }
        finally { busy=false; }
    }
    public async Task<bool> Flush(CancellationToken cancellation=default)
    {
        DeliveryError=null;
        var draft = await FlushDraft(cancellation);
        lock(stateGate) { if(!durable) return false; }
        if (disposed || journalDelivery is null) return draft;
        if (historyBusy) return false;
        historyBusy = true;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,cancellation); limit.CancelAfter(TimeSpan.FromSeconds(8));
            var sent = await journalDelivery(limit.Token);
            if (!disposed && (draft || !lastFailure)) status(sent && draft ? "Схема, результаты и история сохранены у преподавателя." : "Часть данных передана; история продолжает отправляться.");
            return sent && draft;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (draft || !lastFailure) DeliveryError=e is ApiException or InvalidDataException or IOException ? "История сохранена на ПК: " + e.Message : "История сохранена на ПК и ожидает связи с преподавателем.";
            if (!disposed && DeliveryError is { } message) status(message);
            return false;
        }
        finally { historyBusy = false; }
    }
    public async Task<bool> Drain()
    {
        // Finish an already running upload before logout, then send edits/events
        // queued while it was in flight. A failed network attempt stays on disk.
        async Task<bool> Idle()
        {
            var waited=busy||historyBusy;
            for(var i=0;(busy||historyBusy)&&!disposed&&i<400;i++)await Task.Delay(25);
            return !disposed&&!busy&&!historyBusy&&!(waited&&(lastFailure||DeliveryError is not null));
        }
        if(!await Idle())return false;
        for(var batch=0;Pending&&!disposed&&batch<11;batch++)
        {
            if(!await Idle())return false;
            int count;bool dirty;lock(stateGate){count=state.Reports.Count;dirty=state.Dirty;}
            if(await Flush()) return true;
            lock(stateGate)
            {
                if(lastFailure||DeliveryError is not null)return false;
                if(count==state.Reports.Count&&dirty==state.Dirty&&!busy&&!historyBusy&&!(journalDelivery is not null&&journalPending?.Invoke()==true))return false;
            }
        }
        return !Pending;
    }
    void Persist()
    {
        durable=false;
        var json=Wire.Write(state);
        if(System.Text.Encoding.UTF8.GetByteCount(json)>64_000_000) throw new IOException("Очередь результатов заполнена. Исходные файлы сохранены; восстановите связь с преподавателем.");
        ElectronicsHistoryStore.Mirror(path,json);
        durable=true;
    }
    public void Dispose() { if(disposed)return; disposed=true; timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); }
}

