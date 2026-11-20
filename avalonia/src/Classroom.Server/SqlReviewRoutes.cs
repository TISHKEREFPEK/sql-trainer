using Classroom.Contracts;
using Classroom.Domain;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class SqlReviewRoutes
{
    public static void Map(RouteGroupBuilder api, ServerState state, WorkerRunner worker)
    {
        api.MapPost("/teacher/sql-review/attempts/{id}/execute", async (HttpContext ctx, string id, SqlReviewRun input) =>
        {
            state.Authorize(ctx, "teacher");
            if (input.Code.Length > 12000) throw new HttpFault(400, "SQL: не более 12000 символов.");
            var source = await state.Read(async db =>
            {
                var attempt = await db.Attempts.FindAsync(id) ?? throw new HttpFault(404, "SQL-попытка не найдена.");
                var evidence = await db.SqlExecutionEvidence.FindAsync(id);
                if (evidence?.BeforeHash is null) throw new HttpFault(409, "Исходное состояние этой попытки не сохранялось. Точное воспроизведение недоступно.");
                var database = await db.SqlExecutionStates.FindAsync(attempt.StudentId, evidence.BeforeHash) ?? throw new HttpFault(409, "Исходный снимок попытки недоступен.");
                return new WorkerRequest(Catalog.Read(evidence.TaskJson), database.Base64, input.Code, Check: false);
            });
            // The frozen database is loaded only by the isolated SQL worker.
            // Each run starts there; no snapshot is promoted into pupil data.
            return (await worker.Run(source, ctx.RequestAborted)).Result;
        });
        api.MapGet("/teacher/sql-review/attempts/{id}", (HttpContext ctx, string id) =>
        {
            state.Authorize(ctx, "teacher");
            return state.Read(async db =>
            {
                var attempt = await db.Attempts.SingleOrDefaultAsync(a => a.Id == id && (a.Action == "run" || a.Action == "check" || a.Action == null && a.Kind != "clipboard" && a.Kind != "capture"))
                    ?? throw new HttpFault(404, "SQL-попытка не найдена.");
                var student = await db.Profiles.FindAsync(attempt.StudentId) ?? throw new HttpFault(404, "Ученик не найден.");
                var evidence = await db.SqlExecutionEvidence.FindAsync(id);
                var task = evidence is null ? null : Catalog.Read(evidence.TaskJson);
                return new SqlReviewAttempt(attempt.Id, attempt.StudentId, student.FullName ?? student.Login, attempt.TaskId, attempt.LessonId, attempt.WorkScope, attempt.At,
                    attempt.Action ?? "check", attempt.Kind, attempt.Code, task?.Title ?? "Задание " + attempt.TaskId, task?.Prompt, task?.VariantIndex ?? (task is null ? null : 1),
                    evidence?.Revision, evidence is null ? "legacy" : evidence.BeforeHash is null ? "missing-state" : "exact", evidence is null ? null : Wire.Read<ExecutionDto>(evidence.ResultJson));
            });
        });
    }
}
