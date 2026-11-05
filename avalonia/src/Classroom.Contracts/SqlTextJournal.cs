namespace Classroom.Contracts;

public sealed record SqlTextChange(string Id, long Sequence, long At, string Actor, string Kind,
    string BeforeHash, string AfterHash, int Offset, int Removed, string Inserted, string? Checkpoint = null);

public sealed record SqlTextJournal(int Version, string StreamId, long StartedAt, string BaseCode, SqlTextChange[] Changes)
{
    static void ValidateText(string code)
    {
        try { _ = new System.Text.UTF8Encoding(false, true).GetByteCount(code); }
        catch (System.Text.EncoderFallbackException e) { throw new InvalidDataException("В тексте есть неполный символ. Завершите ввод или отмените изменение.", e); }
    }
    public static SqlTextJournal Start(string code, long at)
    { ValidateText(code); return new(1, Guid.NewGuid().ToString("N"), at, code, []); }
    public static SqlTextJournal Record(SqlTextJournal? journal, string before, string after, string actor, string kind, long at, bool force = false)
    {
        journal ??= Start(before, at);
        ValidateText(before); ValidateText(after);
        if (journal.Version != 1 || (journal.Changes.Length == 0 ? SqlSyncHash.Text(journal.BaseCode) : journal.Changes[^1].AfterHash) != SqlSyncHash.Text(before))
            throw new InvalidDataException("Исходный текст не совпадает с последним шагом SQL-истории.");
        if (before == after && !force) return journal;
        if (after.Length > 12000) throw new InvalidDataException("SQL слишком длинный для истории.");
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        if (prefix > 0 && (prefix < before.Length && char.IsHighSurrogate(before[prefix - 1]) && char.IsLowSurrogate(before[prefix]) ||
                          prefix < after.Length && char.IsHighSurrogate(after[prefix - 1]) && char.IsLowSurrogate(after[prefix]))) prefix--;
        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        if (suffix > 0 && (suffix < before.Length && char.IsLowSurrogate(before[before.Length - suffix]) && char.IsHighSurrogate(before[before.Length - suffix - 1]) ||
                          suffix < after.Length && char.IsLowSurrogate(after[after.Length - suffix]) && char.IsHighSurrogate(after[after.Length - suffix - 1]))) suffix--;
        var sequence = journal.Changes.LongLength + 1;
        var change = new SqlTextChange(Guid.NewGuid().ToString("N"), sequence, at, actor, kind,
            SqlSyncHash.Text(before), SqlSyncHash.Text(after), prefix, before.Length - prefix - suffix,
            after.Substring(prefix, after.Length - prefix - suffix), sequence % 64 == 0 ? after : null);
        return journal with { Changes = journal.Changes.Append(change).ToArray() };
    }
    public string Replay(int? through = null)
    {
        if (Version != 1 || !Guid.TryParseExact(StreamId, "N", out _) || StartedAt <= 0 || BaseCode is null || Changes is null || BaseCode.Length > 12000)
            throw new InvalidDataException("Некорректная SQL-история.");
        var count = through ?? Changes.Length;
        if (count < 0 || count > Changes.Length) throw new ArgumentOutOfRangeException(nameof(through));
        var code = BaseCode; var ids = new HashSet<string>();
        ValidateText(code);
        for (var i = 0; i < count; i++)
        {
            var change = Changes[i];
            if (change is null || change.Sequence != i + 1 || !Guid.TryParseExact(change.Id, "N", out _) || !ids.Add(change.Id) || change.At <= 0 ||
                change.Actor is not ("student" or "server" or "recovery") || string.IsNullOrEmpty(change.Kind) ||
                change.BeforeHash != SqlSyncHash.Text(code) || change.Offset < 0 || change.Offset > code.Length || change.Removed < 0 ||
                change.Removed > code.Length - change.Offset || change.Inserted is null)
                throw new InvalidDataException("Нарушен порядок или исходное состояние SQL-истории.");
            code = code.Remove(change.Offset, change.Removed).Insert(change.Offset, change.Inserted);
            ValidateText(code);
            if (code.Length > 12000 || SqlSyncHash.Text(code) != change.AfterHash || change.Checkpoint is { } checkpoint && checkpoint != code)
                throw new InvalidDataException("SQL-история не восстанавливает сохранённый текст.");
        }
        return code;
    }
}
