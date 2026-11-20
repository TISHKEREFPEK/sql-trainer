using System.Security.Cryptography;
using System.Text;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Classroom.Tests;

public sealed class ElectronicsProjectStorageTests
{
    [Theory]
    [InlineData("project-hash")]
    [InlineData("creation-hash")]
    [InlineData("owner-role")]
    [InlineData("revision")]
    [InlineData("permission")]
    [InlineData("phantom-review")]
    [InlineData("snapshot-hash")]
    [InlineData("snapshot-revision")]
    [InlineData("participant")]
    [InlineData("opened-date")]
    [InlineData("saved-date")]
    [InlineData("copy-revision")]
    [InlineData("copy-context")]
    [InlineData("report-copy")]
    public async Task HealthRejectsCorruptionInsteadOfRebindingOrDroppingFreeWork(string damage)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new ClassroomDb(new DbContextOptionsBuilder<ClassroomDb>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        const string json="""{"version":1,"name":"Project","code":"","parts":[],"wires":[]}""";
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var id=Guid.NewGuid().ToString("N");var lesson=Guid.NewGuid().ToString("N");var client=Guid.NewGuid().ToString("N");
        db.Profiles.Add(new(){Id="s",Login="student",PasswordHash=Passwords.Hash(Guid.NewGuid().ToString("N"))});
        db.Profiles.Add(new(){Id="teacher",Login="teacher",Role="teacher",PasswordHash=Passwords.Hash(Guid.NewGuid().ToString("N"))});
        db.LessonSessions.Add(new(){Id=lesson,TeacherId="teacher",GroupName="Test",State="running",CreatedAt=100,StartedAt=150});
        db.LessonParticipants.Add(new(){SessionId=lesson,StudentId="s",JoinedAt=200,LastSeenAt=200});
        var project=new ElectronicsProjectEntity{StudentId="s",Id=id,CreationHash=hash,InitialProjectJson=json,ProjectJson=json,ProjectHash=hash,Name="Project",Revision=1,CreatedAt=200,UpdatedAt=300};
        var snapshot=new ElectronicsPracticeEntity{StudentId="s",ProjectId=id,LessonId=lesson,ProjectJson=json,ProjectHash=hash,Name="Project",Revision=1,OpenedAt=200,FirstSavedAt=200,UpdatedAt=300};
        var work=new ElectronicsWorkEntity{StudentId="s",ClientId=client,FreeProjectId=id,LessonId=lesson,ProjectRevision=1,Sequence=1,ProjectJson=json,ProjectHash=hash,Name="Project",UpdatedAt=300};
        db.ElectronicsProjects.Add(project);db.ElectronicsPractice.Add(snapshot);db.ElectronicsWorks.Add(work);
        await db.SaveChangesAsync();DatabaseHealth.Validate(db);
        switch(damage)
        {
            case "project-hash":project.ProjectHash="wrong";break;
            case "creation-hash":project.CreationHash="wrong";break;
            case "owner-role":(await db.Profiles.SingleAsync(p=>p.Id=="s")).Role="teacher";break;
            case "revision":project.Revision=-1;break;
            case "permission":project.StateVersion=0;break;
            case "phantom-review":project.State="accepted";break;
            case "snapshot-hash":snapshot.ProjectHash="wrong";break;
            case "snapshot-revision":snapshot.Revision=2;break;
            case "participant":db.LessonParticipants.Remove(await db.LessonParticipants.SingleAsync());break;
            case "opened-date":snapshot.OpenedAt=301;break;
            case "saved-date":snapshot.FirstSavedAt=301;break;
            case "copy-revision":work.ProjectRevision=2;break;
            case "copy-context":work.LessonId=null;break;
            case "report-copy":db.ElectronicsReports.Add(new(){StudentId="s",Id=Guid.NewGuid().ToString("N"),ClientId=Guid.NewGuid().ToString("N"),FreeProjectId=id,LessonId=lesson,Kind="compiled",ProjectJson=json,ProjectHash=hash,Name="Project",At=300});break;
        }
        await db.SaveChangesAsync();
        Assert.Throws<InvalidDataException>(()=>DatabaseHealth.Validate(db));
    }
}
