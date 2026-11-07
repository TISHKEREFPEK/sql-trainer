using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsLocalCopiesTests
{
    internal static string Json(string code){var p=CircuitProject.Blink();p.Code=code;return p.Serialize();}
    internal static string Seed(string root,ElectronicsHistoryContext context,LiveClassroom f,string code,string? report=null)
    {
        var file=Path.Combine(root,"electronics",context.ProjectFileName);var json=Json(code);
        using var history=new ElectronicsHistoryStore(file+".history",context,json);
        using var queue=new ElectronicsSync(f.Student,file+".sync.json",_=>{},false,freeContext:context.FreeContext());queue.QueueDraft(json);
        if(report is not null)queue.Record("submitted",json,expectedAssignmentVersion:1,reportId:report);
        ElectronicsHistoryStore.Mirror(file,json);return file;
    }
    internal static Dictionary<string,string> Hashes(string file)=>new[]{file,file+".sync.json",file+".sync.json.backup"}.Where(File.Exists)
        .Concat(Directory.EnumerateFiles(file+".history","*",SearchOption.AllDirectories)).ToDictionary(p=>p,p=>SqlSyncHash.Text(File.ReadAllText(p)));
    internal static void Unchanged(Dictionary<string,string> files){foreach(var file in files)Assert.Equal(file.Value,SqlSyncHash.Text(File.ReadAllText(file.Key)));}
    [Fact]
    public async Task CatalogPagesOnlyThisProfilesProjectAcrossLessonsAndIsolatesDamagedCopies()
    {
        await using var f=await LiveClassroom.Start();var root=Path.Combine(Path.GetTempPath(),"classroom-local-catalog-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");
        var context=new ElectronicsHistoryContext(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null,id);var directory=Path.Combine(root,"electronics");
        var entries=new List<string>();
        for(var i=0;i<23;i++)entries.Add(Seed(root,context with{LessonId=Guid.NewGuid().ToString("N")},f,"// copy "+i));
        var foreign=Seed(root,context with{ProfileId=Guid.NewGuid().ToString("N")},f,"// other pupil");
        Seed(root,context with{Fingerprint=new string('B',64)},f,"// other server");Seed(root,context with{FreeProjectId=Guid.NewGuid().ToString("N")},f,"// other project");
        var first=ElectronicsCopySelection.LocalPage(directory,context);var second=ElectronicsCopySelection.LocalPage(directory,context,first.NextOffset);
        Assert.Equal(20,first.Entries.Length);Assert.True(first.HasMore);Assert.Equal(3,second.Entries.Length);Assert.False(second.HasMore);
        var all=first.Entries.Concat(second.Entries).ToArray();Assert.Equal(23,all.Select(e=>e.Context.LessonId).Distinct().Count());Assert.DoesNotContain(all,e=>e.ProjectFile==foreign);
        var healthy=ElectronicsCopySelection.InspectLocal(all[0]);Assert.NotNull(healthy.ProjectJson);Assert.Null(healthy.Problem);
        var index=Directory.GetFiles(all[1].ProjectFile+".history","c-*.json").Single();File.WriteAllText(index,"broken history");
        var fallback=ElectronicsCopySelection.InspectLocal(all[1]);Assert.NotNull(fallback.ProjectJson);Assert.Contains("только файл схемы",fallback.Problem);Assert.Null(fallback.Recovery);
        File.WriteAllText(all[2].ProjectFile+".sync.json","broken queue");
        var retained=ElectronicsCopySelection.InspectLocal(all[2]);Assert.NotNull(retained.ProjectJson);Assert.NotNull(retained.Problem);Assert.Null(retained.Recovery);
        File.WriteAllText(all[3].ProjectFile,"broken mirror");File.WriteAllText(Directory.GetFiles(all[3].ProjectFile+".history","c-*.json").Single(),"broken index");
        var damaged=ElectronicsCopySelection.InspectLocal(all[3]);Assert.Null(damaged.ProjectJson);Assert.NotNull(damaged.Problem);
        Assert.Equal(23,ElectronicsCopySelection.LocalPage(directory,context).Entries.Length+second.Entries.Length);
    }
    [Fact]
    public async Task ExplicitRecoveryChecksViewedFilesRetainsPointerSourcesAndDeliversOnlyChosenOriginalIds()
    {
        await using var f=await LiveClassroom.Start();var root=Path.Combine(Path.GetTempPath(),"classroom-choice-restore-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");var report=Guid.NewGuid().ToString("N");
        var context=new ElectronicsHistoryContext(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null,id);var directory=Path.Combine(root,"electronics");
        var remote=await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(Json("// remote")));
        var original=Seed(root,context,f,"// local original",report);var newer=ElectronicsCopySelection.SelectServer(directory,context,original,remote,f.Student);
        var otherFiles=Hashes(newer.Path);var pointer=ElectronicsCopySelection.PointerPath(directory,context);var backup=File.ReadAllBytes(pointer+".backup");
        File.WriteAllText(pointer,"damaged pointer");
        var entry=ElectronicsCopySelection.LocalPage(directory,context).Entries.Single(e=>e.ProjectFile==original);
        var viewed=ElectronicsCopySelection.InspectLocal(entry);var candidate=Assert.IsType<ElectronicsSelectionCandidate>(viewed.Recovery);Assert.Equal(1,candidate.Submissions);
        File.WriteAllText(pointer,"changed after preview");Assert.Throws<IOException>(()=>ElectronicsCopySelection.RestoreLocalChoice(candidate));
        candidate=ElectronicsCopySelection.InspectLocal(entry).Recovery!;
        using(var editor=new ElectronicsHistoryStore(original+".history",context,null))Assert.Throws<IOException>(()=>ElectronicsCopySelection.RestoreLocalChoice(candidate));
        File.AppendAllText(original+".sync.json"," ");Assert.Throws<IOException>(()=>ElectronicsCopySelection.RestoreLocalChoice(candidate));
        candidate=ElectronicsCopySelection.InspectLocal(entry).Recovery!;var queue=File.ReadAllBytes(original+".sync.json");
        var restored=ElectronicsCopySelection.RestoreLocalChoice(candidate);Assert.Equal(original,restored.Path);Assert.Equal(restored,ElectronicsCopySelection.Resolve(directory,context));
        Assert.Equal(queue,File.ReadAllBytes(original+".sync.json"));Unchanged(otherFiles);
        Assert.Contains(Directory.GetFiles(directory,Path.GetFileName(pointer)+".damaged-*"),p=>File.ReadAllText(p)=="changed after preview");
        Assert.Contains(Directory.GetFiles(directory,Path.GetFileName(pointer)+".backup.damaged-*"),p=>File.ReadAllBytes(p).SequenceEqual(backup));
        using(var history=new ElectronicsHistoryStore(original+".history",context,null))Assert.Equal("local-selection-restored",history.Journal.Changes.Last().Kind);
        await using(var background=new ElectronicsBackgroundDelivery(root,f.Student))for(var i=0;i<3;i++)Assert.Null((await background.DrainOnce()).Error);
        var detail=await f.Admin.Get<ElectronicsStudentDetail>("teacher/electronics/"+f.StudentId);Assert.Equal(report,Assert.Single(detail.Reports).Id);
        Assert.Equal("// local original",CircuitProject.Parse((await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson).Code);Unchanged(otherFiles);
        Assert.Throws<IOException>(()=>ElectronicsCopySelection.RestoreLocalChoice(candidate));
    }
    [Fact]
    public async Task MissingPointerNeedsExplicitChoiceAndForeignOrChangedHistoryCannotBeRestored()
    {
        await using var f=await LiveClassroom.Start();var root=Path.Combine(Path.GetTempPath(),"classroom-choice-missing-"+Guid.NewGuid().ToString("N"));var id=Guid.NewGuid().ToString("N");
        var context=new ElectronicsHistoryContext(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null,id);var directory=Path.Combine(root,"electronics");
        var remote=await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(Json("// remote")));
        var original=Seed(root,context,f,"// retained");var selected=ElectronicsCopySelection.SelectServer(directory,context,original,remote,f.Student);var pointer=ElectronicsCopySelection.PointerPath(directory,context);File.Delete(pointer);
        Assert.Throws<InvalidDataException>(()=>ElectronicsCopySelection.Resolve(directory,context));
        var entry=ElectronicsCopySelection.LocalPage(directory,context).Entries.Single(e=>e.ProjectFile==selected.Path);var candidate=ElectronicsCopySelection.InspectLocal(entry).Recovery!;Assert.Null(candidate.PointerHash);
        Assert.Throws<InvalidDataException>(()=>ElectronicsCopySelection.RestoreLocalChoice(candidate with{Entry=entry with{Context=entry.Context with{ProfileId=Guid.NewGuid().ToString("N")}}}));
        using(var history=new ElectronicsHistoryStore(selected.Path+".history",selected.Context,null))history.Record(Json("// changed after preview"),"code-edit");
        Assert.Throws<IOException>(()=>ElectronicsCopySelection.RestoreLocalChoice(candidate));Assert.False(File.Exists(pointer));
        candidate=ElectronicsCopySelection.InspectLocal(entry).Recovery!;ElectronicsCopySelection.RestoreLocalChoice(candidate);
        Assert.Equal(selected.Path,ElectronicsCopySelection.Resolve(directory,context).Path);Assert.Equal("// changed after preview",CircuitProject.Parse(ElectronicsCopySelection.InspectLocal(entry).ProjectJson!).Code);
    }
}
