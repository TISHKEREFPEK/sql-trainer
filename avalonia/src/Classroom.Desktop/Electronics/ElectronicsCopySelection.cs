using Classroom.Client;
using Classroom.Contracts;

namespace Classroom.Desktop.Electronics;

internal sealed record ElectronicsCopyLocation(string Path,ElectronicsHistoryContext Context,long? SelectedAt=null,int? ServerRevision=null);
internal sealed record ElectronicsLocalCopy(ElectronicsHistoryContext Context,string ProjectJson,string Title,long UpdatedAt);

// A pointer changes only after a complete new copy is durable. Old copies are
// retained byte-for-byte and are never automatically uploaded once inactive.
internal static class ElectronicsCopySelection
{
    sealed record Selection(int Version,ElectronicsHistoryContext Context,string? PreviousCopyId,long At,int? ServerRevision);
    sealed record Envelope(string Payload,string Hash);
    static ElectronicsHistoryContext Binding(ElectronicsHistoryContext context)
    {
        context.Validate();if(context.FreeProjectId is null)throw new InvalidDataException("Выбор копии доступен только свободному проекту.");
        return context with{LocalCopyId=null};
    }
    static void Regular(string file)
    {if(File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))throw new InvalidDataException("Выбор локальной копии через файловую ссылку запрещён. Исходники сохранены.");}
    static string DirectoryPath(string directory)
    {
        var path=System.IO.Path.GetFullPath(directory);
        if(!string.Equals(System.IO.Path.GetFileName(path),"electronics",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Некорректная папка локальных схем.");
        if(Directory.Exists(path))Regular(path);
        var parent=System.IO.Path.GetDirectoryName(path)!;if(Directory.Exists(parent))Regular(parent);
        return path;
    }
    internal static string PointerPath(string directory,ElectronicsHistoryContext context)=>System.IO.Path.Combine(DirectoryPath(directory),System.IO.Path.ChangeExtension(Binding(context).ProjectFileName,".active.json"));
    public static ElectronicsCopyLocation Resolve(string directory,ElectronicsHistoryContext context)
    {
        var binding=Binding(context);directory=DirectoryPath(directory);var pointer=PointerPath(directory,binding);
        if(!File.Exists(pointer))
        {
            var prefix=System.IO.Path.GetFileNameWithoutExtension(binding.ProjectFileName)+"-copy-*";
            if(Directory.Exists(directory)&&Directory.EnumerateFileSystemEntries(directory,prefix).Any())
                throw new InvalidDataException("Найдены отдельные локальные копии без указателя выбора. Исходники сохранены; требуется восстановление выбора.");
            return new(System.IO.Path.Combine(directory,binding.ProjectFileName),binding);
        }
        Regular(pointer);if(new FileInfo(pointer).Length>16384)throw new InvalidDataException("Повреждён указатель выбранной копии.");
        var envelope=Wire.Read<Envelope>(File.ReadAllText(pointer));
        if(envelope.Payload is null||envelope.Hash!=SqlSyncHash.Text(envelope.Payload))throw new InvalidDataException("Повреждён указатель выбранной копии. Автоматический возврат к старой очереди запрещён.");
        var value=Wire.Read<Selection>(envelope.Payload);
        if(value.Version!=1||value.Context is null||Binding(value.Context)!=binding||value.At<=0||value.At>DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()||
            value.ServerRevision is <0||value.PreviousCopyId is { }previous&&(!Guid.TryParseExact(previous,"N",out _)||previous==value.Context.LocalCopyId))
            throw new InvalidDataException("Указатель относится к другому ученику, проекту или занятию.");
        var selected=System.IO.Path.Combine(directory,value.Context.ProjectFileName);
        if(!File.Exists(selected)&&!File.Exists(selected+".sync.json")&&!File.Exists(selected+".sync.json.backup")&&!Directory.Exists(selected+".history"))
            throw new InvalidDataException("Отсутствуют файлы выбранной локальной копии. Указатель и прежние копии сохранены; требуется восстановление.");
        return new(selected,value.Context,value.At,value.ServerRevision);
    }
    static void Write(string pointer,Selection selection)
    {
        var json=Wire.Write(selection);ElectronicsHistoryStore.Mirror(pointer,Wire.Write(new Envelope(json,SqlSyncHash.Text(json))));
    }
    internal static FileStream Lock(string directory,ElectronicsHistoryContext context)
    {
        var pointer=PointerPath(directory,context);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pointer)!);DirectoryPath(directory);
        var mutex=System.IO.Path.ChangeExtension(pointer,".selection.lock");if(File.Exists(mutex))Regular(mutex);
        return new(mutex,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    public static ElectronicsLocalCopy[] ReadArchives(string directory,ElectronicsHistoryContext context,string activePath)
    {
        var binding=Binding(context);directory=DirectoryPath(directory);
        if(!Directory.Exists(directory))return [];
        var prefix=System.IO.Path.GetFileNameWithoutExtension(binding.ProjectFileName);
        var copies=new List<ElectronicsLocalCopy>();
        foreach(var candidate in Directory.EnumerateDirectories(directory,prefix+"*.json.history").Order(StringComparer.Ordinal))
        {
            var file=candidate[..^8];if(string.Equals(file,activePath,StringComparison.OrdinalIgnoreCase))continue;
            var name=System.IO.Path.GetFileName(file);string? copyId=null;
            if(name!=binding.ProjectFileName)
            {
                var start=prefix+"-copy-";
                if(!name.StartsWith(start,StringComparison.Ordinal)||!Guid.TryParseExact(name[start.Length..^5],"N",out _))continue;
                copyId=name[start.Length..^5];
            }
            var expected=binding with{LocalCopyId=copyId};Regular(candidate);
            var indexes=Directory.EnumerateFiles(candidate,"c-*.json").ToArray();
            if(indexes.Length!=1)throw new InvalidDataException("Не удалось прочитать локальные копии: отсутствует однозначный индекс истории. Исходники сохранены.");
            using var history=ElectronicsHistoryStore.OpenExisting(candidate,System.IO.Path.GetFileName(indexes[0]),binding.ProfileId,binding.Fingerprint);
            if(history.Context!=expected)throw new InvalidDataException("Контекст локальной копии не совпадает с её именем. Исходники сохранены.");
            var project=CircuitProject.Parse(history.ProjectJson);var journal=history.Journal;
            copies.Add(new(expected,history.ProjectJson,project.Name,journal.Changes.LastOrDefault()?.At??journal.StartedAt));
        }
        return copies.OrderByDescending(c=>c.UpdatedAt).ToArray();
    }
    public static ElectronicsCopyLocation SelectServer(string directory,ElectronicsHistoryContext context,string expectedActivePath,ElectronicsProjectDto server,ClassroomApi api)
    {
        var binding=Binding(context);directory=DirectoryPath(directory);
        if(api.ProfileId!=binding.ProfileId||api.Connection.Fingerprint!=binding.Fingerprint||api.LessonId!=binding.LessonId||api.Token is null)
            throw new InvalidDataException("Выбор серверной копии относится к другому ученическому сеансу.");
        if(server.Id!=binding.FreeProjectId||server.Revision<0||server.StateVersion<1)throw new InvalidDataException("Серверная версия относится к другому проекту.");
        CircuitProject.Parse(server.ProjectJson);
        Directory.CreateDirectory(directory);DirectoryPath(directory);
        var pointer=PointerPath(directory,binding);
        using var selectionLease=Lock(directory,binding);
        var current=Resolve(directory,binding);
        if(!string.Equals(current.Path,System.IO.Path.GetFullPath(expectedActivePath),StringComparison.OrdinalIgnoreCase))
            throw new IOException("Выбранная копия уже изменилась. Обновите список перед переключением.");
        // The editor must release its lease first. No data is moved or deleted.
        using var source=new ElectronicsHistoryStore(current.Path+".history",current.Context,null);
        var client=ElectronicsHistoryStore.InspectBindings(current.Path+".history",current.Context).ClientId;
        ElectronicsSync.ReadOutbox(current.Path+".sync.json",null,client,current.Context.FreeContext());
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // This unchanged base marker distinguishes interrupted preparation from
        // a lost pointer. A missing pointer plus fragments always fails closed.
        if(!File.Exists(pointer))Write(pointer,new(1,current.Context,null,now,null));
        var selected=binding with{LocalCopyId=Guid.NewGuid().ToString("N")};var file=System.IO.Path.Combine(directory,selected.ProjectFileName);
        using(var history=new ElectronicsHistoryStore(file+".history",selected,server.ProjectJson))
        using(var sync=new ElectronicsSync(api,file+".sync.json",_=>{},false,freeContext:selected.FreeContext(server.Revision)))
        {
            history.Record(server.ProjectJson,"server-version-prepared",true);
            sync.QueueDraft(server.ProjectJson);ElectronicsHistoryStore.Mirror(file,server.ProjectJson);
            var selectedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Write(pointer,new(1,selected,current.Context.LocalCopyId,selectedAt,server.Revision));
            history.Record(server.ProjectJson,"server-version-selected",true);
            return new(file,selected,selectedAt,server.Revision);
        }
    }
}
