using System.Security.Cryptography;
using System.Text;
using Classroom.Contracts;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class ElectronicsProjects
{
    public static string Id(string value)=>Guid.TryParseExact(value,"N",out var id)?id.ToString("N"):throw new HttpFault(400,"Некорректный номер свободной схемы или занятия.");
    static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static ElectronicsProjectDto Dto(ElectronicsProjectEntity p)=>new(p.Id,p.Name,p.ProjectJson,p.Revision,p.State,p.CanSubmit,p.StateVersion,p.CreatedAt,p.UpdatedAt);
    public static async Task<ElectronicsProjectEntity> Own(ClassroomDb db,Session session,string id)=>await db.ElectronicsProjects.FindAsync(session.ProfileId,Id(id))??throw new HttpFault(404,"Свободная схема не найдена.");
    public static async Task Member(ClassroomDb db,Session session,string lessonId)
    {if(!await db.LessonParticipants.AnyAsync(p=>p.SessionId==lessonId&&p.StudentId==session.ProfileId))throw new HttpFault(403,"Схема не относится к исходному занятию ученика.");}
    public static ElectronicsPracticeEntity Snapshot(ElectronicsProjectEntity p,string lessonId,long? openedAt=null)=>new(){StudentId=p.StudentId,ProjectId=p.Id,LessonId=lessonId,ProjectJson=p.ProjectJson,ProjectHash=p.ProjectHash,Name=p.Name,Parts=p.Parts,Wires=p.Wires,Revision=p.Revision,OpenedAt=openedAt,UpdatedAt=ServerState.Now};
    public static void Map(RouteGroupBuilder api,ServerState state)
    {
        api.MapGet("/electronics/projects",(HttpContext ctx,int? offset)=>
        {
            var session=state.Authorize(ctx,"student");var start=offset??0;if(start is <0 or >1000000)throw new HttpFault(400,"Некорректная страница проектов.");
            return state.Read(async db=>{var rows=await db.ElectronicsProjects.Where(p=>p.StudentId==session.ProfileId).OrderByDescending(p=>p.UpdatedAt).ThenBy(p=>p.Id).Skip(start).Take(31).Select(p=>new ElectronicsProjectSummary(p.Id,p.Name,p.Revision,p.State,p.CanSubmit,p.StateVersion,p.UpdatedAt)).ToArrayAsync();return new ElectronicsProjectList(rows.Take(30).ToArray(),rows.Length>30,start+Math.Min(30,rows.Length));});
        });
        api.MapGet("/electronics/projects/{id}",(HttpContext ctx,string id)=>{var session=state.Authorize(ctx,"student");return state.Read(async db=>Dto(await Own(db,session,id)));});
        api.MapPut("/electronics/projects/{id}",(HttpContext ctx,string id,ElectronicsProjectCreate input)=>
        {
            var session=state.Authorize(ctx,"student");id=Id(id);var metadata=ElectronicsProjectFormat.Inspect(input.ProjectJson);var hash=Hash(input.ProjectJson);
            return state.Write(async db=>
            {
                await LessonRoutes.RequireWork(state,db,session,"electronics");var project=await db.ElectronicsProjects.FindAsync(session.ProfileId,id);
                if(project is not null){if(project.CreationHash!=hash)throw new HttpFault(409,"Этот номер уже использован для другого проекта.");return Dto(project);}
                project=new(){StudentId=session.ProfileId,Id=id,CreationHash=hash,InitialProjectJson=input.ProjectJson,ProjectJson=input.ProjectJson,ProjectHash=hash,Name=metadata.Name,Parts=metadata.Parts,Wires=metadata.Wires,CreatedAt=ServerState.Now,UpdatedAt=ServerState.Now};db.ElectronicsProjects.Add(project);
                var snapshot=Snapshot(project,session.LessonId!);snapshot.FirstSavedAt=project.CreatedAt;db.ElectronicsPractice.Add(snapshot);return Dto(project);
            });
        });
        api.MapPost("/electronics/projects/{id}/open",(HttpContext ctx,string id,ElectronicsProjectOpen input)=>
        {
            var session=state.Authorize(ctx,"student");id=Id(id);var lesson=Id(input.LessonId);LessonRoutes.CheckContext(session,lesson);
            return state.Write(async db=>
            {
                await LessonRoutes.RequireWork(state,db,session,"electronics");var project=await Own(db,session,id);var snapshot=await db.ElectronicsPractice.FindAsync(session.ProfileId,id,lesson);
                if(snapshot is null)db.ElectronicsPractice.Add(Snapshot(project,lesson,ServerState.Now));else snapshot.OpenedAt??=ServerState.Now;
                return Dto(project);
            });
        });
    }
}
