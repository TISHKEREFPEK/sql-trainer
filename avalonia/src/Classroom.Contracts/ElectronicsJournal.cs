namespace Classroom.Contracts;

public sealed record ElectronicsSerialIo(int Port, string Text);

public sealed record ElectronicsChange(string Id, long Sequence, long At, string Actor, string Kind, string BeforeHash, string AfterHash,
    int Offset, int Removed, string Inserted, string? Checkpoint = null, string? GestureId = null, string? ReportId = null, string? ReportHash = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ElectronicsSerialIo? SerialIo = null);

public sealed record ElectronicsJournal(int Version, string StreamId, long StartedAt, string BaseJson, ElectronicsChange[] Changes)
{
    static void ValidateSerial(string json,string kind,ElectronicsSerialIo? data)
    {
        if(data is null)return;
        if(kind is not ("serial-input-request" or "serial-input" or "serial-output" or "serial-clear" or "serial-channel")||data.Port is <0 or >3||data.Text is null||data.Text.Length>16384)
            throw new InvalidDataException("Некорректные данные последовательного порта в истории.");
        using var document=System.Text.Json.JsonDocument.Parse(json);
        var kinds=document.RootElement.GetProperty("parts").EnumerateArray().Select(p=>p.GetProperty("kind").GetString()).ToArray();
        if(data.Port>=(kinds.Contains("Mega")?4:1)||kind is "serial-input-request" or "serial-input" or "serial-output"&&!kinds.Any(k=>k is "Uno" or "Mega"))
            throw new InvalidDataException("Последовательный порт не относится к плате этой схемы.");
        try{_ = new System.Text.UTF8Encoding(false,true).GetByteCount(data.Text);}
        catch(System.Text.EncoderFallbackException e){throw new InvalidDataException("В данных порта есть неполный символ.",e);}
    }
    static void Validate(string json)
    {
        try { _ = new System.Text.UTF8Encoding(false, true).GetByteCount(json); }
        catch (System.Text.EncoderFallbackException e) { throw new InvalidDataException("В схеме есть неполный символ. Новое изменение не сохранено.", e); }
        ElectronicsProjectFormat.Inspect(json);
    }
    public static ElectronicsJournal Start(string json, long at)
    { Validate(json); return new(1, Guid.NewGuid().ToString("N"), at, json, []); }
    public static ElectronicsJournal Record(ElectronicsJournal journal, string before, string after, string kind, long at,
        string actor = "student", bool force = false, string? gestureId = null, string? reportId = null, string? reportHash = null, ElectronicsSerialIo? serialIo = null)
    {
        Validate(after);
        ValidateSerial(after,kind,serialIo);
        if (journal.Version != 1 || SqlSyncHash.Text(before) != (journal.Changes.Length == 0 ? SqlSyncHash.Text(journal.BaseJson) : journal.Changes[^1].AfterHash))
            throw new InvalidDataException("Исходное состояние не совпадает с историей схемы.");
        if (kind.Length is < 1 or > 80 || actor is not ("student" or "teacher" or "server" or "recovery") || at <= 0 || at > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
            gestureId is not null && !Guid.TryParseExact(gestureId, "N", out _) || reportId is not null && (!Guid.TryParseExact(reportId, "N", out _) || reportHash?.Length != 64 || !reportHash.All(Uri.IsHexDigit)) || reportId is null && reportHash is not null)
            throw new InvalidDataException("Некорректное событие истории схемы.");
        if (before == after && !force) return journal;
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        if (prefix > 0 && (prefix < before.Length && char.IsSurrogatePair(before, prefix - 1) || prefix < after.Length && char.IsSurrogatePair(after, prefix - 1))) prefix--;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        if (suffix > 0 && (suffix < before.Length && char.IsSurrogatePair(before, before.Length - suffix - 1) || suffix < after.Length && char.IsSurrogatePair(after, after.Length - suffix - 1))) suffix--;
        var sequence = journal.Changes.LongLength + 1;
        var step = new ElectronicsChange(Guid.NewGuid().ToString("N"), sequence, at, actor, kind, SqlSyncHash.Text(before), SqlSyncHash.Text(after),
            prefix, before.Length - prefix - suffix, after.Substring(prefix, after.Length - prefix - suffix), sequence % 64 == 0 ? after : null, gestureId, reportId, reportHash,serialIo);
        return journal with { Changes = journal.Changes.Append(step).ToArray() };
    }
    public string Replay(int? through = null)
    {
        if (Version != 1 || !Guid.TryParseExact(StreamId, "N", out _) || StartedAt <= 0 || StartedAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() || Changes is null || BaseJson is null)
            throw new InvalidDataException("Некорректная история схемы.");
        var count = through ?? Changes.Length;
        if (count < 0 || count > Changes.Length) throw new ArgumentOutOfRangeException(nameof(through));
        var json = BaseJson; Validate(json); var ids = new HashSet<string>();
        for (var i = 0; i < count; i++)
        {
            var step = Changes[i];
            if (step is null || step.Sequence != i + 1 || !Guid.TryParseExact(step.Id, "N", out _) || !ids.Add(step.Id) || step.At <= 0 || step.At > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() ||
                step.Actor is not ("student" or "teacher" or "server" or "recovery") || string.IsNullOrEmpty(step.Kind) || step.Kind.Length > 80 ||
                step.BeforeHash != SqlSyncHash.Text(json) || step.Offset < 0 || step.Offset > json.Length || step.Removed < 0 || step.Removed > json.Length - step.Offset || step.Inserted is null ||
                step.GestureId is not null && !Guid.TryParseExact(step.GestureId, "N", out _) || step.ReportId is not null && (!Guid.TryParseExact(step.ReportId, "N", out _) || step.ReportHash?.Length != 64 || !step.ReportHash.All(Uri.IsHexDigit)) || step.ReportId is null && step.ReportHash is not null)
                throw new InvalidDataException("Нарушен порядок или исходное состояние истории схемы.");
            json = json.Remove(step.Offset, step.Removed).Insert(step.Offset, step.Inserted); Validate(json);
            ValidateSerial(json,step.Kind,step.SerialIo);
            if (SqlSyncHash.Text(json) != step.AfterHash || step.Checkpoint is { } checkpoint && checkpoint != json)
                throw new InvalidDataException("История не восстанавливает сохранённую схему.");
        }
        return json;
    }
}
