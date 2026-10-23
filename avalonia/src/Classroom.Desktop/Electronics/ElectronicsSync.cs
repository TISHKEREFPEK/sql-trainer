using Classroom.Client;
using Classroom.Contracts;
using Avalonia.Threading;
namespace Classroom.Desktop.Electronics;

public sealed class ElectronicsSync : IDisposable
{
    public sealed class Outbox
    {
        public string ClientId { get; set; }=Guid.NewGuid().ToString("N");
        public long Sequence { get; set; }
        public string ProjectJson { get; set; }="";
        public bool Dirty { get; set; }
        public List<ElectronicsReportRequest> Reports { get; set; }=[];
    }
    readonly ClassroomApi api;
    readonly string path;
    readonly Action<string> status;
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(2)};
    readonly CancellationTokenSource lifetime=new();
    Outbox state=new();
    bool busy,disposed,lastFailure;
    public bool Pending => state.Dirty||state.Reports.Count>0;
    public ElectronicsSync(ClassroomApi api,string path,Action<string> status,bool startTimer=true)
    {
        this.api=api; this.path=path; this.status=status;
        try
        {
            if(File.Exists(path))
            {
                if(new FileInfo(path).Length>64_000_000) throw new InvalidDataException("Слишком большая очередь.");
                state=Wire.Read<Outbox>(File.ReadAllText(path));
                if(!Guid.TryParseExact(state.ClientId,"N",out _)||state.Sequence<0||state.Reports.Count>100) throw new InvalidDataException("Некорректная очередь результатов.");
                if(state.ProjectJson.Length>0) CircuitProject.Parse(state.ProjectJson);
                foreach(var report in state.Reports) { CircuitProject.Parse(report.ProjectJson); if(!Guid.TryParseExact(report.Id,"N",out _)||!ElectronicsProjectFormat.ReportKinds.Contains(report.Kind)||report.Detail.Length>16000) throw new InvalidDataException("Некорректный отчёт."); }
            }
        }
        catch(Exception e) when(e is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            if(File.Exists(path)) File.Move(path,path+".corrupt-"+Guid.NewGuid().ToString("N"));
            state=new(); status("Повреждённая очередь сохранена отдельно. Работа будет отправлена новой копией.");
        }
        // Re-send the local draft once after login, including after server restore.
        state.Dirty=true;
        timer.Tick+=async(_,_)=>await Flush();
        if(startTimer) timer.Start();
    }
    public void QueueDraft(string json)
    {
        if(disposed) return;
        if(state.ProjectJson!=json) { state.ProjectJson=json; state.Sequence=checked(state.Sequence+1); state.Dirty=true; }
        if(state.Sequence==0) state.Sequence=1;
        Persist(); if(Pending) status("Ожидает передачи преподавателю…");
    }
    public void Record(string kind,string json,string detail="")
    {
        if(disposed) return;
        if(!ElectronicsProjectFormat.ReportKinds.Contains(kind)) throw new InvalidDataException("Неизвестный вид результата.");
        if(state.Reports.Count>=100 || state.Reports.Sum(r=>r.ProjectJson.Length)+(long)json.Length>16_000_000) throw new InvalidDataException("Очередь результатов заполнена. Подключитесь к преподавателю и повторите отправку; локальная схема сохранена.");
        var explanation = detail.Length>16000 ? detail[..15900]+"\n[Сообщение сокращено. Полный текст сохранён в приложении ученика.]" : detail;
        state.Reports.Add(new(Guid.NewGuid().ToString("N"),kind,json,explanation)); Persist(); status("Результат сохранён локально и ожидает передачи.");
    }
    public async Task<bool> Flush()
    {
        if(disposed||busy||!Pending||state.ProjectJson.Length==0) return !Pending;
        busy=true;
        var reports=state.Reports.Take(10).ToArray(); var sequence=state.Sequence; var json=state.ProjectJson;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var ack=await api.Send<ElectronicsSyncReceipt>(HttpMethod.Put,"electronics/work",new ElectronicsSyncRequest(state.ClientId,sequence,json,reports),timeout.Token);
            if(disposed) return false;
            lastFailure=false;
            if(state.Sequence==sequence && state.ProjectJson==json) state.Dirty=false;
            var ids=ack.ReportIds.ToHashSet(); state.Reports.RemoveAll(r=>ids.Contains(r.Id)); Persist();
            status(Pending?"Часть результатов передана; очередь отправляется…":"Схема и результаты сохранены у преподавателя."); return !Pending;
        }
        catch(Exception e) when(e is HttpRequestException or OperationCanceledException or ApiException or IOException or InvalidDataException)
        {
            lastFailure=true;
            if(!disposed) status(e is ApiException a && a.Status is 400 or 409 ? a.Message : e is ApiException auth && auth.Status==401 ? "Войдите снова для передачи результатов. Локальная схема сохранена." : "Нет связи: работа и результаты сохранены локально, отправка повторится.");
            return false;
        }
        finally { busy=false; }
    }
    public async Task<bool> Drain()
    {
        // Finish an already running upload before logout, then send edits/events
        // queued while it was in flight. A failed network attempt stays on disk.
        var waited=busy;
        for(var i=0;busy&&!disposed&&i<240;i++) await Task.Delay(25);
        if(disposed||busy||waited&&lastFailure) return false;
        for(var batch=0;Pending&&!disposed&&batch<11;batch++)
        {
            var count=state.Reports.Count; var dirty=state.Dirty;
            if(await Flush()) return true;
            if(lastFailure||count==state.Reports.Count&&dirty==state.Dirty) return false;
        }
        return !Pending;
    }
    void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path+".tmp",Wire.Write(state)); File.Move(path+".tmp",path,true);
    }
    public void Dispose() { if(disposed)return; disposed=true; timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); }
}

