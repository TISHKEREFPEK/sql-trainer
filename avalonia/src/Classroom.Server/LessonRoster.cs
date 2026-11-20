using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class LessonRoster
{
    static string Name(ProfileEntity student) => string.IsNullOrWhiteSpace(student.FullName) ? student.Login : student.FullName;
    public static async Task Add(ClassroomDb db, LessonSessionEntity lesson, ProfileEntity student, string source)
    {
        if (db.LessonRoster.Local.Any(r => r.LessonId == lesson.Id && r.StudentId == student.Id) || await db.LessonRoster.FindAsync(lesson.Id, student.Id) is not null) return;
        var group = student.GroupId is null ? null : await db.Groups.FindAsync(student.GroupId);
        db.LessonRoster.Add(new() { LessonId = lesson.Id, StudentId = student.Id, StudentName = Name(student),
            GroupId = group?.Id, GroupName = group?.Name ?? "Без группы", AddedAt = ServerState.Now, Source = source });
    }
    public static async Task Capture(ClassroomDb db, LessonSessionEntity lesson, bool atStart)
    {
        if (lesson.RosterCapturedAt is null)
        {
            lesson.RosterCapturedAt = ServerState.Now; lesson.RosterAtStart = atStart;
            // A legacy entry retained a group name, not its historical ID. Never
            // substitute the pupil's current group for that missing identifier.
            foreach (var member in await db.LessonParticipants.Where(p => p.SessionId == lesson.Id).ToArrayAsync())
            {
                var pupil = await db.Profiles.FindAsync(member.StudentId);
                if (pupil is null || db.LessonRoster.Local.Any(r => r.LessonId == lesson.Id && r.StudentId == pupil.Id) || await db.LessonRoster.FindAsync(lesson.Id, pupil.Id) is not null) continue;
                db.LessonRoster.Add(new() { LessonId = lesson.Id, StudentId = pupil.Id, StudentName = Name(pupil),
                    GroupName = string.IsNullOrWhiteSpace(member.GroupNameAtEntry) ? "Группа не записана" : member.GroupNameAtEntry, GroupKnown = false, Source = "legacy-participant", AddedAt = ServerState.Now });
            }
        }
        if (lesson.GroupId is not null)
            foreach (var pupil in await db.Profiles.Where(p => p.Role == "student" && p.GroupId == lesson.GroupId).ToArrayAsync()) await Add(db, lesson, pupil, "selected-group");
    }
    public static async Task Enrolled(ClassroomDb db, ProfileEntity student)
    {
        var lesson = await db.LessonSessions.SingleOrDefaultAsync(l => l.EndedAt == null && l.StartedAt != null);
        if (lesson is not null && lesson.GroupId is not null && lesson.GroupId == student.GroupId) await Add(db, lesson, student, "registered");
    }
}
