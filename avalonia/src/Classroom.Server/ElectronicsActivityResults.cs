using Classroom.Contracts;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

internal static class ElectronicsActivityResults
{
    internal static int? Seconds(IEnumerable<ElectronicsActiveSpan> source)
    {
        var spans=source.OrderBy(s=>s.StartedAt).ThenBy(s=>s.EndedAt).ToArray();if(spans.Length==0)return null;
        long total=0,start=spans[0].StartedAt,end=spans[0].EndedAt;
        foreach(var span in spans.Skip(1))
        {
            if(span.StartedAt<=end)end=Math.Max(end,span.EndedAt);
            else{total+=end-start;start=span.StartedAt;end=span.EndedAt;}
        }
        total+=end-start;return (int)Math.Min(int.MaxValue,total/1000);
    }
    internal static async Task Add(ClassroomDb db,List<ResultsWork> works)
    {
        var ids=works.Where(w=>w.Subject=="electronics").Select(w=>w.StudentId).Distinct().ToArray();if(ids.Length==0)return;
        // Only light interval columns are loaded. Never retrieve JSON/code merely
        // to calculate time. Overlapping/forked/delivered-again spans count once.
        var rows=await (from change in db.ElectronicsJournalChanges.AsNoTracking()
            join stream in db.ElectronicsJournalStreams on new{change.StudentId,Id=change.StreamId} equals new{stream.StudentId,stream.Id}
            join copy in db.ElectronicsWorks on new{stream.StudentId,stream.ClientId} equals new{copy.StudentId,copy.ClientId}
            where ids.Contains(change.StudentId)&&change.ActivityStartedAt!=null&&change.ActivityEndedAt!=null
            select new{change.StudentId,stream.AssignmentId,stream.LessonId,copy.FreeProjectId,Start=change.ActivityStartedAt!.Value,End=change.ActivityEndedAt!.Value}).ToArrayAsync();
        for(var i=0;i<works.Count;i++)
        {
            var work=works[i];if(work.Subject!="electronics")continue;
            var own=rows.Where(r=>r.StudentId==work.StudentId&&(work.AssignmentId is not null?r.AssignmentId==work.AssignmentId:
                work.FreeProjectId is not null&&r.FreeProjectId==work.FreeProjectId)&&(work.LessonId is null||r.LessonId==work.LessonId));
            works[i]=work with{ActiveSeconds=Seconds(own.Select(r=>new ElectronicsActiveSpan(r.Start,r.End)))};
        }
    }
}
