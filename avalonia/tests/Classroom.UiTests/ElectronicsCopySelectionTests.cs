using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using System.Text.Json.Nodes;
using Xunit;

namespace Classroom.UiTests;

public sealed class ElectronicsCopySelectionTests
{
    static string Root()=>Path.Combine(Path.GetTempPath(),"classroom-copy-choice-"+Guid.NewGuid().ToString("N"));
    static ElectronicsHistoryContext Context(LiveClassroom f,string id)=>new(f.StudentId,f.Student.Connection.Fingerprint,f.Student.LessonId!,null,id);
    static string Json(string code){var project=CircuitProject.Blink();project.Code=code;return project.Serialize();}
    static string Seed(string directory,ElectronicsHistoryContext context,LiveClassroom f,string json)
    {
        var path=Path.Combine(directory,context.ProjectFileName);
        using var history=new ElectronicsHistoryStore(path+".history",context,CircuitProject.Blink().Serialize());history.Record(json,"code-edit");
        using var sync=new ElectronicsSync(f.Student,path+".sync.json",_=>{},false,freeContext:context.FreeContext());
        sync.QueueDraft(json);sync.Record("submitted",json,expectedAssignmentVersion:1);ElectronicsHistoryStore.Mirror(path,json);return path;
    }
    static Dictionary<string,string> Files(string path)
    {
        var files=new[]{path,path+".sync.json",path+".sync.json.backup"}.Where(File.Exists).Concat(Directory.EnumerateFiles(path+".history","*",SearchOption.AllDirectories));
        return files.ToDictionary(p=>p,p=>SqlSyncHash.Text(File.ReadAllText(p)));
    }
    static void Unchanged(Dictionary<string,string> files){foreach(var file in files)Assert.Equal(file.Value,SqlSyncHash.Text(File.ReadAllText(file.Key)));}
    static void Cleanup(string root)
    {
        var absolute=Path.GetFullPath(root);var temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        Assert.StartsWith(temp,absolute,StringComparison.OrdinalIgnoreCase);Assert.StartsWith("classroom-copy-choice-",Path.GetFileName(absolute));
        if(Directory.Exists(absolute))Directory.Delete(absolute,true);
    }
    [Fact]
    public async Task SelectionRetainsOriginalBytesAndFailedOrStaleCommitKeepsTheActiveCopy()
    {
        await using var f=await LiveClassroom.Start();var root=Root();var directory=Path.Combine(root,"electronics");
        try
        {
            var id=Guid.NewGuid().ToString("N");var context=Context(f,id);var remote=await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(Json("// remote")));
            var path=Seed(directory,context,f,Json("// unsent local"));var original=Files(path);var local=ElectronicsSync.ReadOutbox(path+".sync.json",null,freeContext:context.FreeContext());
            using(var opened=new ElectronicsHistoryStore(path+".history",context,null))
                Assert.Throws<IOException>(()=>ElectronicsCopySelection.SelectServer(directory,context,path,remote,f.Student));
            var selected=ElectronicsCopySelection.SelectServer(directory,context,path,remote,f.Student);
            Assert.NotNull(selected.Context.LocalCopyId);Assert.NotEqual(path,selected.Path);Assert.Equal(selected,ElectronicsCopySelection.Resolve(directory,context));Unchanged(original);
            var fresh=ElectronicsSync.ReadOutbox(selected.Path+".sync.json",null,freeContext:context.FreeContext());Assert.NotEqual(local.ClientId,fresh.ClientId);Assert.Empty(fresh.Reports);Assert.Equal(remote.ProjectJson,fresh.ProjectJson);
            Assert.Equal(remote.Revision,fresh.FreeContext!.ExpectedRevision);
            using(var history=new ElectronicsHistoryStore(selected.Path+".history",selected.Context,null))
            {Assert.Equal(remote.ProjectJson,history.ProjectJson);Assert.Equal(new[]{"server-version-prepared","server-version-selected"},history.Journal.Changes.Select(c=>c.Kind));}
            Assert.Throws<IOException>(()=>ElectronicsCopySelection.SelectServer(directory,context,path,remote,f.Student));
            var pointer=ElectronicsCopySelection.PointerPath(directory,context);
            using(var locked=new FileStream(pointer,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var error=Assert.ThrowsAny<Exception>(()=>ElectronicsCopySelection.SelectServer(directory,context,selected.Path,remote,f.Student));
                Assert.True(error is IOException or UnauthorizedAccessException);Assert.Contains(error.HResult&0xffff,new[]{5,32,33});
            }
            Assert.Equal(selected,ElectronicsCopySelection.Resolve(directory,context));Unchanged(original);
            var bytes=File.ReadAllBytes(pointer);File.WriteAllText(pointer,"broken");Assert.ThrowsAny<Exception>(()=>ElectronicsCopySelection.Resolve(directory,context));
            Unchanged(original);File.WriteAllBytes(pointer,bytes);
            var envelope=JsonNode.Parse(File.ReadAllText(pointer))!;var payload=JsonNode.Parse(envelope["payload"]!.GetValue<string>())!;
            payload["context"]!["localCopyId"]=Guid.NewGuid().ToString("N");var json=payload.ToJsonString();envelope["payload"]=json;envelope["hash"]=SqlSyncHash.Text(json);File.WriteAllText(pointer,envelope.ToJsonString());
            Assert.Throws<InvalidDataException>(()=>ElectronicsCopySelection.Resolve(directory,context));Unchanged(original);
        }
        finally{Cleanup(root);}
    }
    [Fact]
    public async Task RecoveryRestoresOnlyTheSelectedGenerationWithoutReactivatingAnArchivedQueue()
    {
        await using var f=await LiveClassroom.Start();var root=Root();var directory=Path.Combine(root,"electronics");
        try
        {
            var id=Guid.NewGuid().ToString("N");var context=Context(f,id);var remote=await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(Json("// remote")));
            var original=Seed(directory,context,f,Json("// original"));var inactiveCandidate=ElectronicsOutboxRecovery.Inspect(original,context,"test");Assert.NotNull(inactiveCandidate.Backup);
            var selected=ElectronicsCopySelection.SelectServer(directory,context,original,remote,f.Student);var originalFiles=Files(original);
            Assert.Throws<IOException>(()=>ElectronicsOutboxRecovery.Restore(inactiveCandidate));
            string client;
            using(var sync=new ElectronicsSync(f.Student,selected.Path+".sync.json",_=>{},false,freeContext:context.FreeContext()))
            {sync.QueueDraft(remote.ProjectJson);client=sync.ClientId;}
            File.WriteAllText(selected.Path+".sync.json","damaged selected queue");
            var problem=Assert.Throws<ElectronicsOutboxRecoveryException>(()=>ElectronicsOutboxRecovery.Ensure(selected.Path,selected.Context,null));Assert.NotNull(problem.Candidate.Backup);
            var damaged=ElectronicsOutboxRecovery.Restore(problem.Candidate);Assert.NotNull(damaged);Assert.Equal("damaged selected queue",File.ReadAllText(damaged));
            Assert.Equal(client,ElectronicsSync.ReadOutbox(selected.Path+".sync.json",null,freeContext:context.FreeContext()).ClientId);
            Assert.Equal(selected,ElectronicsCopySelection.Resolve(directory,context));Unchanged(originalFiles);
        }
        finally{Cleanup(root);}
    }
    [Fact]
    public async Task BackgroundUploadsOnlyTheSelectedGenerationAndNeverReactivatesOldReports()
    {
        await using var f=await LiveClassroom.Start();var root=Root();var directory=Path.Combine(root,"electronics");
        try
        {
            var id=Guid.NewGuid().ToString("N");var context=Context(f,id);var remote=await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(Json("// selected server")));
            var path=Seed(directory,context,f,Json("// old pending submission"));var original=Files(path);
            var old=ElectronicsSync.ReadOutbox(path+".sync.json",null,freeContext:context.FreeContext());
            var selected=ElectronicsCopySelection.SelectServer(directory,context,path,remote,f.Student);
            await using(var background=new ElectronicsBackgroundDelivery(root,f.Student))
                for(var i=0;i<4;i++)Assert.Null((await background.DrainOnce()).Error);
            var detail=await f.Admin.Get<ElectronicsStudentDetail>("teacher/electronics/"+f.StudentId);Assert.Empty(detail.Reports);
            var work=Assert.Single(detail.Works);Assert.NotEqual(old.ClientId,work.ClientId);Assert.Equal(remote.ProjectJson,(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson);
            var stream=Assert.Single(await f.Admin.Get<ElectronicsJournalInfo[]>($"teacher/electronics-journal/{f.StudentId}?projectId={id}"));Assert.Equal(work.ClientId,stream.ClientId);
            Unchanged(original);Assert.Single(ElectronicsSync.ReadOutbox(path+".sync.json",null,freeContext:context.FreeContext()).Reports);
            Assert.Equal(selected.Context,ElectronicsCopySelection.Resolve(directory,context).Context);
            File.WriteAllText(ElectronicsCopySelection.PointerPath(directory,context),"broken");
            await using(var background=new ElectronicsBackgroundDelivery(root,f.Student))Assert.NotNull((await background.DrainOnce()).Error);
            Assert.Empty((await f.Admin.Get<ElectronicsStudentDetail>("teacher/electronics/"+f.StudentId)).Reports);Unchanged(original);
        }
        finally{Cleanup(root);}
    }
    [Fact]
    public async Task PauseWaitsForCurrentHistoryAndRetainsLaterEditsUntilExplicitResume()
    {
        await using var f=await LiveClassroom.Start();var root=Root();Directory.CreateDirectory(root);
        try
        {
            var id=Guid.NewGuid().ToString("N");var context=Context(f,id);var initial=Json("// before pause");await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(initial));
            var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
            using var sync=new ElectronicsSync(f.Student,Path.Combine(root,"queue.json"),_=>{},false,freeContext:context.FreeContext(),journalDelivery:async token=>
            {if(Interlocked.Increment(ref calls)==1){entered.SetResult();await release.Task.WaitAsync(token);}return true;},journalPending:()=>calls<1);
            sync.QueueDraft(initial);var sending=sync.Flush();await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));var pause=sync.PauseDelivery();Assert.False(pause.IsCompleted);
            var later=Json("// edits while paused");sync.QueueDraft(later);Assert.False(await sync.Flush());release.SetResult();await sending;Assert.True(await pause);
            Assert.True(sync.Pending);Assert.Equal(initial,(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson);
            sync.ResumeDelivery();Assert.True(await sync.Drain());Assert.Equal(later,(await f.Student.Get<ElectronicsProjectDto>("electronics/projects/"+id)).ProjectJson);
        }
        finally{Cleanup(root);}
    }
    [Fact]
    public async Task FreeMirrorOrQueueWithoutHistoryCannotSilentlyCreateANewIdentity()
    {
        await using var f=await LiveClassroom.Start();var root=Root();var directory=Path.Combine(root,"electronics");Directory.CreateDirectory(directory);
        try
        {
            var id=Guid.NewGuid().ToString("N");var context=Context(f,id);var json=Json("// retained fragment");
            await f.Student.Put<ElectronicsProjectDto>("electronics/projects/"+id,new ElectronicsProjectCreate(json));
            var path=Path.Combine(directory,context.ProjectFileName);File.WriteAllText(path,json);
            Assert.Throws<ElectronicsOutboxRecoveryException>(()=>ElectronicsOutboxRecovery.Ensure(path,context,null));
            using(var sync=new ElectronicsSync(f.Student,path+".sync.json",_=>{},false,freeContext:context.FreeContext()))sync.QueueDraft(json);
            var bytes=File.ReadAllBytes(path+".sync.json");var problem=Assert.Throws<ElectronicsOutboxRecoveryException>(()=>ElectronicsOutboxRecovery.Ensure(path,context,null));Assert.Null(problem.Candidate.Backup);
            Assert.Empty(Directory.GetFiles(path+".history","c-*.json"));Assert.Equal(bytes,File.ReadAllBytes(path+".sync.json"));
            await using var background=new ElectronicsBackgroundDelivery(root,f.Student);Assert.NotNull((await background.DrainOnce()).Error);Assert.Empty((await f.Admin.Get<ElectronicsStudentDetail>("teacher/electronics/"+f.StudentId)).Works);
        }
        finally{Cleanup(root);}
    }
}
