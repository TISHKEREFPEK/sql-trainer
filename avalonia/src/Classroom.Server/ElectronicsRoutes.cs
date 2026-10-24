using System.Security.Cryptography;
using System.Text;
using Classroom.Contracts;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;
namespace Classroom.Server;

public static class ElectronicsRoutes
{
    static string Id(string value) => Guid.TryParse(value,out var id) ? id.ToString("N") : throw new HttpFault(400,"Некорректный идентификатор работы.");
    static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    public static void Map(RouteGroupBuilder api,ServerState state)
    {
        api.MapPut("/electronics/work", async (HttpContext ctx,ElectronicsSyncRequest input) =>
        {
            var session=state.Authorize(ctx,"student"); var client=Id(input.ClientId);
            if(input.Sequence<1 || input.Reports.Length>10) throw new HttpFault(400,"Некорректная очередь результатов.");
            var metadata=ElectronicsProjectFormat.Inspect(input.ProjectJson); var hash=Hash(input.ProjectJson);
            var reports=input.Reports.Select(r=>
            {
                if(!ElectronicsProjectFormat.ReportKinds.Contains(r.Kind)||r.Detail.Length>16000) throw new HttpFault(400,"Некорректный результат запуска.");
                var info=ElectronicsProjectFormat.Inspect(r.ProjectJson); return (Id:Id(r.Id),r.Kind,r.ProjectJson,r.Detail,info.Name,Hash:Hash(r.ProjectJson));
            }).ToArray();
            if(reports.Select(r=>r.Id).Distinct().Count()!=reports.Length) throw new HttpFault(400,"Повтор результата в очереди.");
            return await state.Write(async db=>
            {
                var work=await db.ElectronicsWorks.SingleOrDefaultAsync(w=>w.StudentId==session.ProfileId&&w.ClientId==client);
                if(work is not null && (input.Sequence<work.Sequence || input.Sequence==work.Sequence&&hash!=work.ProjectHash)) throw new HttpFault(409,"На сервере есть другая версия этой копии. Локальная схема сохранена; не заменяйте её старой копией.");
                if(work is null)
                {
                    if(await db.ElectronicsWorks.CountAsync(w=>w.StudentId==session.ProfileId)>=10) throw new HttpFault(409,"Для ученика уже сохранены 10 копий работ с разных устройств.");
                    work=new(){StudentId=session.ProfileId,ClientId=client}; db.ElectronicsWorks.Add(work);
                }
                if(input.Sequence>work.Sequence)
                {
                    work.Sequence=input.Sequence; work.ProjectJson=input.ProjectJson; work.ProjectHash=hash; work.Name=metadata.Name; work.Parts=metadata.Parts; work.Wires=metadata.Wires; work.UpdatedAt=ServerState.Now;
                }
                foreach(var report in reports)
                {
                    var existing=await db.ElectronicsReports.SingleOrDefaultAsync(r=>r.StudentId==session.ProfileId&&r.Id==report.Id);
                    if(existing is not null)
                    {
                        if(existing.ClientId!=client||existing.ProjectHash!=report.Hash||existing.Kind!=report.Kind||existing.Detail!=report.Detail) throw new HttpFault(409,"Этот номер результата уже использован для другой операции.");
                        continue;
                    }
                    db.ElectronicsReports.Add(new(){StudentId=session.ProfileId,ClientId=client,Id=report.Id,Kind=report.Kind,ProjectJson=report.ProjectJson,ProjectHash=report.Hash,Detail=report.Detail,Name=report.Name,At=ServerState.Now});
                }
                return new ElectronicsSyncReceipt(work.Sequence,work.UpdatedAt,reports.Select(r=>r.Id).ToArray());
            });
        });
        api.MapGet("/teacher/electronics", async (HttpContext ctx)=>
        {
            state.Authorize(ctx,"teacher");
            return await state.Read(async db=>
            {
                var students=await db.Profiles.Where(p=>p.Role=="student").Select(p=>new{p.Id,p.Login,p.GroupId}).ToArrayAsync();
                var works=await db.ElectronicsWorks.Select(w=>new{w.StudentId,w.UpdatedAt}).ToArrayAsync();
                var reports=await db.ElectronicsReports.Select(r=>new{r.StudentId,r.Kind,r.At,r.Id}).ToArrayAsync();
                return new ElectronicsOverview(await db.Groups.OrderBy(g=>g.Name).Select(g=>new GroupDto(g.Id,g.Name)).ToArrayAsync(),students.OrderBy(s=>s.Login).Select(s=>
                {
                    var ws=works.Where(w=>w.StudentId==s.Id).ToArray(); var rs=reports.Where(r=>r.StudentId==s.Id).ToArray(); var last=rs.OrderByDescending(r=>r.At).ThenByDescending(r=>r.Id).FirstOrDefault();
                    var saved=ws.Select(w=>w.UpdatedAt).DefaultIfEmpty().Max(); var at=Math.Max(saved,last?.At??0);
                    return new ElectronicsStudentResult(s.Id,s.Login,s.GroupId,ws.Length,rs.Count(r=>r.Kind=="submitted"),rs.Count(r=>r.Kind=="compiled"),rs.Count(r=>r.Kind is "compile-error" or "run-error"),at==0?null:at,last is not null&&last.At>=saved?last.Kind:ws.Length>0?"draft":null);
                }).ToArray());
            });
        });
        api.MapGet("/teacher/electronics/{studentId}", (HttpContext ctx,string studentId,int? offset)=>
        {
            state.Authorize(ctx,"teacher"); var start=offset??0; if(start<0||start>1000000) throw new HttpFault(400,"Некорректная страница.");
            return state.Read(async db=>
            {
                var student=await db.Profiles.SingleOrDefaultAsync(p=>p.Id==studentId&&p.Role=="student")??throw new HttpFault(404,"Ученик не найден.");
                var works=await db.ElectronicsWorks.Where(w=>w.StudentId==studentId).OrderByDescending(w=>w.UpdatedAt).Select(w=>new ElectronicsWorkInfo(w.ClientId,w.Name,w.Parts,w.Wires,w.UpdatedAt)).ToArrayAsync();
                var reports=await db.ElectronicsReports.Where(r=>r.StudentId==studentId).OrderByDescending(r=>r.At).ThenByDescending(r=>r.Id).Skip(start).Take(31).Select(r=>new ElectronicsReportInfo(r.Id,r.ClientId,r.Name,r.Kind,r.At)).ToArrayAsync();
                return new ElectronicsStudentDetail(student.Id,student.Login,student.GroupId,works,reports.Take(30).ToArray(),reports.Length>30,start+Math.Min(30,reports.Length));
            });
        });
        api.MapGet("/teacher/electronics/{studentId}/work/{clientId}", (HttpContext ctx,string studentId,string clientId)=>
        {
            state.Authorize(ctx,"teacher"); var id=Id(clientId);
            return state.Read(async db=>
            {
                var w=await db.ElectronicsWorks.SingleOrDefaultAsync(w=>w.StudentId==studentId&&w.ClientId==id)??throw new HttpFault(404,"Работа не найдена.");
                return new ElectronicsSnapshot(w.ProjectJson,"draft",w.UpdatedAt,"");
            });
        });
        api.MapGet("/teacher/electronics/{studentId}/report/{reportId}", (HttpContext ctx,string studentId,string reportId)=>
        {
            state.Authorize(ctx,"teacher"); var id=Id(reportId);
            return state.Read(async db=>
            {
                var r=await db.ElectronicsReports.SingleOrDefaultAsync(r=>r.StudentId==studentId&&r.Id==id)??throw new HttpFault(404,"Результат не найден.");
                return new ElectronicsSnapshot(r.ProjectJson,r.Kind,r.At,r.Detail);
            });
        });
    }
}
