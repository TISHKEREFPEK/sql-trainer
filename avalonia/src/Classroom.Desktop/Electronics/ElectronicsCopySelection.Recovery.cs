using Classroom.Contracts;
using System.Security.Cryptography;

namespace Classroom.Desktop.Electronics;

internal sealed record ElectronicsLocalEntry(string ProjectFile,ElectronicsHistoryContext Context,long UpdatedAt);
internal sealed record ElectronicsLocalPage(ElectronicsLocalEntry[] Entries,bool HasMore,int NextOffset);
internal sealed record ElectronicsSelectionCandidate(ElectronicsLocalEntry Entry,string? PointerHash,string? PointerBackupHash,
    string HistoryHash,string QueueHash,string ClientId,string ProjectJson,int Reports,int Submissions);
internal sealed record ElectronicsLocalSnapshot(ElectronicsLocalEntry Entry,string? ProjectJson,string? Problem,bool Active=false,ElectronicsSelectionCandidate? Recovery=null);

internal static partial class ElectronicsCopySelection
{
    static bool Expected(Exception e)=>e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException;
    static string Problem(Exception e)=>e is System.Text.Json.JsonException?"Файл копии повреждён: неверный формат данных.":e.Message;
    static string? FileHash(string file)
    {if(!File.Exists(file))return null;Regular(file);using var input=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(input));}
    static void BoundEntry(ElectronicsLocalEntry entry)
    {
        entry.Context.Validate();Binding(entry.Context);var directory=DirectoryPath(System.IO.Path.GetDirectoryName(entry.ProjectFile)!);
        if(!string.Equals(System.IO.Path.GetFullPath(entry.ProjectFile),System.IO.Path.Combine(directory,entry.Context.ProjectFileName),StringComparison.Ordinal))
            throw new InvalidDataException("Файл копии не соответствует ученику, проекту или занятию.");
    }
    static ElectronicsLocalEntry[] Discover(string directory,ElectronicsHistoryContext context)
    {
        var binding=Binding(context);directory=DirectoryPath(directory);if(!Directory.Exists(directory))return [];
        var prefix=SqlSyncHash.Text(binding.Fingerprint+":"+binding.ProfileId)+"-free-"+binding.FreeProjectId+"-";
        var entries=new Dictionary<string,ElectronicsLocalEntry>(StringComparer.OrdinalIgnoreCase);
        // Only direct, deterministic names for this pupil/server/project. Never
        // recurse through imports, history pages, damaged archives or links.
        foreach(var path in Directory.EnumerateFileSystemEntries(directory,prefix+"*"))
        {
            var name=System.IO.Path.GetFileName(path);string suffix;
            if(name.EndsWith(".json.history",StringComparison.Ordinal))suffix=".history";
            else if(name.EndsWith(".json.sync.json",StringComparison.Ordinal))suffix=".sync.json";
            else if(name.EndsWith(".json",StringComparison.Ordinal))suffix="";
            else continue;
            var fileName=suffix.Length==0?name:name[..^suffix.Length];var keys=fileName[prefix.Length..^5].Split('-');
            if(keys.Length is not (1 or 3)||!Guid.TryParseExact(keys[0],"N",out _))continue;
            string? copy=null;if(keys.Length==3){if(keys[1]!="copy"||!Guid.TryParseExact(keys[2],"N",out _))continue;copy=keys[2];}
            var copyContext=binding with{LessonId=keys[0],LocalCopyId=copy};
            if(fileName!=copyContext.ProjectFileName)continue;
            var file=System.IO.Path.Combine(directory,fileName);var at=new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
            if(!entries.TryGetValue(file,out var existing)||at>existing.UpdatedAt)entries[file]=new(file,copyContext,at);
        }
        return entries.Values.OrderByDescending(e=>e.UpdatedAt).ThenBy(e=>e.ProjectFile,StringComparer.Ordinal).ToArray();
    }
    public static ElectronicsLocalPage LocalPage(string directory,ElectronicsHistoryContext context,int offset=0)
    {
        if(offset<0)throw new InvalidDataException("Некорректная страница локальных копий.");
        var rows=Discover(directory,context).Skip(offset).Take(21).ToArray();return new(rows.Take(20).ToArray(),rows.Length>20,offset+Math.Min(20,rows.Length));
    }
    static ElectronicsHistoryStore OpenEntry(ElectronicsLocalEntry entry)
    {
        BoundEntry(entry);var folder=entry.ProjectFile+".history";Regular(folder);
        var indexes=Directory.EnumerateFiles(folder,"c-*.json").ToArray();
        if(indexes.Length!=1)throw new InvalidDataException("Отсутствует однозначный индекс истории копии.");
        var history=ElectronicsHistoryStore.OpenExisting(folder,System.IO.Path.GetFileName(indexes[0]),entry.Context.ProfileId,entry.Context.Fingerprint);
        if(history.Context!=entry.Context){history.Dispose();throw new InvalidDataException("Контекст истории не совпадает с именем копии.");}return history;
    }
    public static ElectronicsLocalSnapshot InspectLocal(ElectronicsLocalEntry entry)
    {
        string? json=null;
        try
        {
            BoundEntry(entry);using var history=OpenEntry(entry);json=history.ProjectJson;
            var bindings=ElectronicsHistoryStore.InspectBindings(entry.ProjectFile+".history",entry.Context);
            var queue=ElectronicsSync.ReadOutbox(entry.ProjectFile+".sync.json",null,bindings.ClientId,entry.Context.FreeContext());
            var directory=System.IO.Path.GetDirectoryName(entry.ProjectFile)!;bool active;
            try{active=string.Equals(Resolve(directory,entry.Context).Path,entry.ProjectFile,StringComparison.OrdinalIgnoreCase);}
            catch(Exception e)when(Expected(e))
            {
                var pointer=PointerPath(directory,entry.Context);
                var candidate=new ElectronicsSelectionCandidate(entry,FileHash(pointer),FileHash(pointer+".backup"),bindings.Hash,FileHash(entry.ProjectFile+".sync.json")!,queue.ClientId,json,queue.Reports.Count,queue.Reports.Count(r=>r.Kind=="submitted"));
                return new(entry,json,"Выбор копии не удалось прочитать. Схема, история и очередь этой копии проверены.",Recovery:candidate);
            }
            return new(entry,json,null,active);
        }
        catch(Exception e)when(Expected(e))
        {
            var problem=Problem(e);
            if(json is null)
                try
                {
                    BoundEntry(entry);Regular(entry.ProjectFile);if(new FileInfo(entry.ProjectFile).Length>16_000_000)throw new InvalidDataException("Файл схемы слишком большой.");
                    json=File.ReadAllText(entry.ProjectFile);CircuitProject.Parse(json);
                    return new(entry,json,problem+" Показан только файл схемы; история и очередь не подтверждены. Можно сохранить схему отдельным проектом.");
                }
                catch(Exception fallback)when(Expected(fallback)){problem+=" "+Problem(fallback);json=null;}
            return new(entry,json,problem);
        }
    }
    public static ElectronicsCopyLocation RestoreLocalChoice(ElectronicsSelectionCandidate candidate)
    {
        var entry=candidate.Entry;BoundEntry(entry);var directory=System.IO.Path.GetDirectoryName(entry.ProjectFile)!;
        using var selectionLease=Lock(directory,entry.Context);var pointer=PointerPath(directory,entry.Context);
        void PointerUnchanged()
        {if(FileHash(pointer)!=candidate.PointerHash||FileHash(pointer+".backup")!=candidate.PointerBackupHash)throw new IOException("Выбор изменился после просмотра. Обновите список копий.");}
        PointerUnchanged();bool unresolved;
        try{Resolve(directory,entry.Context);unresolved=false;}catch(Exception e)when(Expected(e)){unresolved=true;}
        if(!unresolved)throw new IOException("Выбор копии уже исправен. Обновите список перед восстановлением.");
        using var history=OpenEntry(entry);
        var bindings=ElectronicsHistoryStore.InspectBindings(entry.ProjectFile+".history",entry.Context);
        var queue=ElectronicsSync.ReadOutbox(entry.ProjectFile+".sync.json",null,bindings.ClientId,entry.Context.FreeContext());
        if(bindings.Hash!=candidate.HistoryHash||FileHash(entry.ProjectFile+".sync.json")!=candidate.QueueHash||history.ProjectJson!=candidate.ProjectJson||queue.ClientId!=candidate.ClientId)
            throw new IOException("Копия изменилась после просмотра. Откройте её заново перед восстановлением.");
        void Archive(string path,string? hash)
        {
            if(hash is null)return;var archive=path+".damaged-"+Guid.NewGuid().ToString("N");
            using(var input=File.OpenRead(path))using(var output=new FileStream(archive,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){input.CopyTo(output);output.Flush(true);}
            if(FileHash(archive)!=hash)throw new IOException("Файл выбора изменился при сохранении исходника. Замена отменена.");
        }
        PointerUnchanged();Archive(pointer,candidate.PointerHash);Archive(pointer+".backup",candidate.PointerBackupHash);PointerUnchanged();
        var at=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();Write(pointer,new(1,entry.Context,null,at,queue.FreeContext!.ExpectedRevision));
        history.Record(history.ProjectJson,"local-selection-restored",true);
        return new(entry.ProjectFile,entry.Context,at,queue.FreeContext.ExpectedRevision);
    }
}
