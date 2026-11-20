using System.Security.Cryptography;
using System.Text;
using Classroom.Contracts;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class ElectronicsReviews
{
    static readonly string[] Decisions = ["accepted", "changes-requested", "rejected"];
    static ElectronicsReviewDecision Decision(ElectronicsReviewEntity row) => new(row.Id, row.Sequence, row.Action, row.Comment, row.Grade, row.At);

    sealed class ReviewRow
    {
        public string StudentId { get; set; } = "";
        public string StudentName { get; set; } = "";
        public string? GroupId { get; set; }
        public string GroupName { get; set; } = "";
        public string ReportId { get; set; } = "";
        public string? AssignmentId { get; set; }
        public string? FreeProjectId { get; set; }
        public string? LessonId { get; set; }
        public string Title { get; set; } = "";
        public long SubmittedAt { get; set; }
        public string State { get; set; } = "";
        public int? Grade { get; set; }
        public ElectronicsReviewItem Dto() => new(StudentId, StudentName, GroupId, GroupName, ReportId, AssignmentId, LessonId, Title, SubmittedAt, State, Grade,FreeProjectId);
    }
    static IQueryable<ReviewRow> Items(ClassroomDb db) =>
        from report in db.ElectronicsReports
        where report.Kind == "submitted"
        join student in db.Profiles on report.StudentId equals student.Id
        join g in db.Groups on student.GroupId equals g.Id into groups
        from pupilGroup in groups.DefaultIfEmpty()
        join a in db.ElectronicsAssignments on report.AssignmentId equals a.Id into assignments
        from assignment in assignments.DefaultIfEmpty()
        join t in db.LessonElectronicsTasks on new { assignment.LessonId, assignment.TaskId } equals new { t.LessonId, t.TaskId } into tasks
        from task in tasks.DefaultIfEmpty()
        select new ReviewRow { StudentId = student.Id, StudentName = student.FullName ?? student.Login, GroupId = student.GroupId, GroupName = pupilGroup == null ? "Без группы" : pupilGroup.Name,
            ReportId = report.Id, AssignmentId = report.AssignmentId,FreeProjectId=report.FreeProjectId, LessonId = assignment == null ? report.LessonId : assignment.LessonId, Title = task == null ? report.Name : task.Title, SubmittedAt = report.At,
            State = db.ElectronicsReviews.Where(r => r.StudentId == report.StudentId && r.ReportId == report.Id && r.Action != "allow-resubmit")
                .OrderByDescending(r => r.Sequence).Select(r => r.Action).FirstOrDefault() ?? "awaiting-review",
            Grade = db.ElectronicsReviews.Where(r => r.StudentId == report.StudentId && r.ReportId == report.Id && r.Action != "allow-resubmit")
                .OrderByDescending(r => r.Sequence).Select(r => r.Grade).FirstOrDefault() };

    public static async Task<ElectronicsReviewDecision?> Latest(ClassroomDb db, string assignmentId)
    {
        var report = await db.ElectronicsReports.Where(r => r.AssignmentId == assignmentId && r.Kind == "submitted")
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Id).Select(r => new { r.Id, r.StudentId }).FirstOrDefaultAsync();
        if (report is null) return null;
        var row = await db.ElectronicsReviews.Where(r => r.StudentId == report.StudentId && r.ReportId == report.Id && r.Action != "allow-resubmit")
            .OrderByDescending(r => r.Sequence).FirstOrDefaultAsync();
        return row is null ? null : Decision(row);
    }

    public static async Task<ElectronicsReviewDetail> Detail(ClassroomDb db, string studentId, string reportId)
    {
        var item = await Items(db).SingleOrDefaultAsync(r => r.StudentId == studentId && r.ReportId == reportId)
            ?? throw new HttpFault(404, "Сданная работа не найдена.");
        var report = await db.ElectronicsReports.FindAsync(studentId, reportId) ?? throw new HttpFault(404, "Сдача не найдена.");
        var history = await db.ElectronicsReviews.Where(r => r.StudentId == studentId && r.ReportId == reportId).OrderBy(r => r.Sequence).ToArrayAsync();
        var assignment = item.AssignmentId is null ? null : await db.ElectronicsAssignments.FindAsync(item.AssignmentId);
        var project=item.FreeProjectId is null?null:await db.ElectronicsProjects.FindAsync(studentId,item.FreeProjectId);
        var prompt = assignment is null ? "Свободная схема. Условие не было закреплено при отправке." :
            (await db.LessonElectronicsTasks.FindAsync(assignment.LessonId, assignment.TaskId))!.Prompt;
        var latest = assignment is not null ? await db.ElectronicsReports.Where(r => r.AssignmentId == assignment.Id && r.Kind == "submitted")
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Id).Select(r => r.Id).FirstAsync() == reportId:
            project is null||await db.ElectronicsReports.Where(r=>r.StudentId==studentId&&r.FreeProjectId==project.Id&&r.Kind=="submitted").OrderByDescending(r=>r.At).ThenByDescending(r=>r.Id).Select(r=>r.Id).FirstAsync()==reportId;
        return new(item.Dto(), prompt, report.ProjectJson, history.Select(Decision).ToArray(), assignment?.CanSubmit ?? project?.CanSubmit ?? false, assignment?.StateVersion??project?.StateVersion, latest);
    }


    public static async Task<ElectronicsReviewDecision> Decide(ClassroomDb db, string teacherId, string studentId, string reportId, string id, ElectronicsReviewRequest input)
    {
        if (!Guid.TryParseExact(id, "N", out _) || !Decisions.Contains(input.Action) && input.Action != "allow-resubmit" || input.Comment.Length > 8000 ||
            input.Grade is < 1 or > 5 || input.Action is "accepted" or "rejected" && input.Grade is null || input.Action == "allow-resubmit" && input.Grade is not null)
            throw new HttpFault(400, "Выберите результат, оценку от 1 до 5 и комментарий до 8000 символов.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Wire.Write(new { studentId, reportId, input }))));
        var duplicate = await db.ElectronicsReviews.FindAsync(id);
        if (duplicate is not null)
        {
            if (duplicate.TeacherId != teacherId || duplicate.RequestHash != hash) throw new HttpFault(409, "Номер решения уже использован для другой операции.");
            return Decision(duplicate);
        }
        var detail = await Detail(db, studentId, reportId);
        if (detail.History.LastOrDefault()?.Id != input.ExpectedReviewId || detail.AssignmentVersion != input.ExpectedAssignmentVersion)
            throw new HttpFault(409, "Работа уже изменилась. Обновите сдачу перед сохранением решения.");
        var assignment = detail.Item.AssignmentId is null ? null : await db.ElectronicsAssignments.FindAsync(detail.Item.AssignmentId);
        var project=detail.Item.FreeProjectId is null?null:await db.ElectronicsProjects.FindAsync(studentId,detail.Item.FreeProjectId);
        if (input.Action == "allow-resubmit" && (assignment is null&&project is null || !detail.IsLatestSubmission || detail.Item.State == "awaiting-review" || (assignment?.CanSubmit??project!.CanSubmit)))
            throw new HttpFault(409, "Разрешить пересдачу можно для последней проверенной работы, если она ещё не разрешена.");
        var row = new ElectronicsReviewEntity { Id = id, StudentId = studentId, ReportId = reportId, TeacherId = teacherId,
            Sequence = (detail.History.LastOrDefault()?.Sequence ?? 0) + 1, PreviousReviewId = input.ExpectedReviewId,
            Action = input.Action, Comment = input.Comment, Grade = input.Grade, RequestHash = hash, At = Math.Max(ServerState.Now, detail.Item.SubmittedAt) };
        db.ElectronicsReviews.Add(row);
        if (assignment is not null && detail.IsLatestSubmission)
        {
            if (input.Action != "allow-resubmit") assignment.State = input.Action;
            assignment.CanSubmit = input.Action is "changes-requested" or "allow-resubmit";
            assignment.StateVersion++;
        }
        if(project is not null&&detail.IsLatestSubmission)
        {
            if(input.Action!="allow-resubmit")project.State=input.Action;
            project.CanSubmit=input.Action is "changes-requested" or "allow-resubmit";project.StateVersion++;
        }
        return Decision(row);
    }

    public static async Task<ElectronicsReviewQueue> Queue(ClassroomDb db, string? studentId = null, string? status = null, string? groupId = null, string? lessonId = null, int offset = 0, string? assignmentId = null,string? projectId=null)
    {
        if (offset < 0 || offset > 1000000 || status is not (null or "pending" or "all")) throw new HttpFault(400, "Некорректный фильтр очереди.");
        var all = Items(db).Where(r => (studentId == null || r.StudentId == studentId) && (assignmentId == null || r.AssignmentId == assignmentId)&&(projectId==null||r.FreeProjectId==projectId));
        var pending = await all.CountAsync(r => r.State == "awaiting-review");
        var query = all.Where(r => (status == "all" || r.State == "awaiting-review") && (groupId == null || r.GroupId == (groupId == "none" ? null : groupId)) && (lessonId == null || r.LessonId == lessonId));
        var rows = await query.OrderByDescending(r => r.SubmittedAt).ThenBy(r => r.StudentId).ThenBy(r => r.ReportId).Skip(offset).Take(31).ToArrayAsync();
        return new ElectronicsReviewQueue(rows.Take(30).Select(r => r.Dto()).ToArray(), pending, rows.Length > 30, offset + Math.Min(30, rows.Length));
    }

    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapGet("/teacher/reviews/electronics/filters", async (HttpContext ctx) =>
        {
            state.Authorize(ctx, "teacher");
            return await state.Read(async db => new ElectronicsReviewFilters(await db.Groups.OrderBy(g => g.Name).Select(g => new GroupDto(g.Id, g.Name)).ToArrayAsync(),
                await db.LessonSessions.OrderByDescending(s => s.CreatedAt).ThenBy(s => s.Id).Select(s => new ElectronicsReviewLesson(s.Id, s.GroupName, s.CreatedAt)).ToArrayAsync()));
        });
        api.MapGet("/teacher/reviews/electronics", (HttpContext ctx, string? status, string? groupId, string? lessonId, string? studentId, string? assignmentId,string? projectId, int? offset) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Read(db => Queue(db, studentId: studentId, status: status, groupId: groupId, lessonId: lessonId, offset: offset ?? 0, assignmentId: assignmentId,projectId:projectId));
        });
        api.MapGet("/teacher/reviews/electronics/{studentId}/{reportId}", (HttpContext ctx, string studentId, string reportId) =>
        {
            state.Authorize(ctx, "teacher"); return state.Read(db => Detail(db, studentId, reportId));
        });
        api.MapGet("/electronics/reviews/{reportId}", (HttpContext ctx, string reportId) =>
        {
            var session = state.Authorize(ctx, "student"); return state.Read(db => Detail(db, session.ProfileId, reportId));
        });
        api.MapPut("/teacher/reviews/electronics/{studentId}/{reportId}/decisions/{id}", (HttpContext ctx, string studentId, string reportId, string id, ElectronicsReviewRequest input) =>
        {
            var teacher = state.Authorize(ctx, "teacher");
            return state.Write(db => Decide(db, teacher.ProfileId, studentId, reportId, id, input));
        });
    }
}
