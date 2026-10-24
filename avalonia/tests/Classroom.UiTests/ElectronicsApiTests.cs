using System.Text.Json;
using Classroom.Client;
using Classroom.Contracts;
using Classroom.Desktop.Electronics;
using Xunit;
namespace Classroom.UiTests;
public sealed class ElectronicsApiTests
{
    [Fact]
    public async Task RolesIdempotencyImmutableReportsAndDeviceCopiesAreEnforced()
    {
        await using var f=await LiveClassroom.Start(); using var anonymous=new ClassroomApi(f.Admin.Connection);
        var first=CircuitExamples.Servo();first.Parts[1]=first.Parts[1] with {X=-5200,Y=-2600,Angle=37,StretchX=1.4,StretchY=0.8}; var json=first.Serialize(); var client=Guid.NewGuid().ToString("N"); var id=Guid.NewGuid().ToString("N"); var request=new ElectronicsSyncRequest(client,1,json,[new(id,"submitted",json,"")]);
        Assert.Equal(401,(await Assert.ThrowsAsync<ApiException>(()=>anonymous.Put<ElectronicsSyncReceipt>("electronics/work",request))).Status);
        Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(()=>f.Admin.Put<ElectronicsSyncReceipt>("electronics/work",request))).Status);
        Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Get<ElectronicsOverview>("teacher/electronics"))).Status);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",request); await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",request);
        var overview=await f.Admin.Get<ElectronicsOverview>("teacher/electronics"); Assert.Equal(1,overview.Students.Single().Submitted);
        first.Code="void setup(){} void loop(){}"; var changed=first.Serialize();
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(client,2,changed,[new(id,"submitted",changed,"")])))).Status);
        Assert.Equal(json,(await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/work/{client}")).ProjectJson);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(client,2,changed,[]));
        Assert.Equal(409,(await Assert.ThrowsAsync<ApiException>(()=>f.Student.Put<ElectronicsSyncReceipt>("electronics/work",request))).Status);
        Assert.Equal(json,(await f.Admin.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/report/{id}")).ProjectJson);
        await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(Guid.NewGuid().ToString("N"),1,json,[]));
        Assert.Equal(2,(await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}")).Works.Length);
        var other=await f.Admin.Post<JsonElement>("teacher/students",new CreateStudentRequest("no-work","synthetic-other-password",null)); var otherId=other.GetProperty("id").GetString()!;
        Assert.Equal(0,(await f.Admin.Get<ElectronicsOverview>("teacher/electronics")).Students.Single(s=>s.StudentId==otherId).WorkCount);
        using var second=new ClassroomApi(f.Admin.Connection); await second.Login(new("student","no-work","synthetic-other-password"));
        Assert.Equal(403,(await Assert.ThrowsAsync<ApiException>(()=>second.Get<ElectronicsSnapshot>($"teacher/electronics/{f.StudentId}/report/{id}"))).Status);
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(()=>second.Put<ElectronicsSyncReceipt>("electronics/work",request with { ProjectJson="{}" }))).Status);
        var tooBig=CircuitProject.Blink(); tooBig.Code=new string('x',100001);
        Assert.Equal(400,(await Assert.ThrowsAsync<ApiException>(()=>second.Put<ElectronicsSyncReceipt>("electronics/work",request with { ProjectJson=tooBig.Serialize() }))).Status);
    }
    [Fact]
    public async Task ReportHistoryPaginatesWithoutRepeatingEntries()
    {
        await using var f=await LiveClassroom.Start(); var json=CircuitProject.Blink().Serialize(); var client=Guid.NewGuid().ToString("N");
        for(var i=0;i<4;i++) await f.Student.Put<ElectronicsSyncReceipt>("electronics/work",new ElectronicsSyncRequest(client,1,json,Enumerable.Range(0,i==3?5:10).Select(_=>new ElectronicsReportRequest(Guid.NewGuid().ToString("N"),"compiled",json,"")).ToArray()));
        var first=await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}"); var next=await f.Admin.Get<ElectronicsStudentDetail>($"teacher/electronics/{f.StudentId}?offset={first.NextOffset}");
        Assert.True(first.HasMore); Assert.Equal(30,first.Reports.Length); Assert.False(next.HasMore); Assert.Equal(5,next.Reports.Length); Assert.Equal(35,first.Reports.Concat(next.Reports).Select(r=>r.Id).Distinct().Count());
    }
}
