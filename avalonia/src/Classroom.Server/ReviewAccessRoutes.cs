using Classroom.Contracts;
using Classroom.Domain;
using Classroom.Storage;
using Microsoft.EntityFrameworkCore;

namespace Classroom.Server;

public static class ReviewAccessRoutes
{
    static string SessionKey(HttpContext ctx) => ReviewAccessRegistry.Hash(ctx.Request.Headers.Authorization.ToString());
    static bool IssuerAlive(ServerState state, string key) => state.Sessions.TryGetValue(key, out var session) && session.Role == "teacher" && session.Expires > ServerState.Now;
    // Match ServerState's key, which hashes the bearer token without the prefix.
    static string BearerKey(HttpContext ctx) => ReviewAccessRegistry.Hash(ctx.Request.Headers.Authorization.ToString()["Bearer ".Length..]);
    static T Access<T>(Func<T> action)
    {
        try { return action(); }
        catch (ReviewAccessException e) { throw new HttpFault(e.RateLimited ? 429 : 403, e.Message); }
    }
    static ReviewLease Authorize(HttpContext ctx, ServerState state)
    {
        var student = state.Authorize(ctx, "student");
        return Access(() => state.ReviewAccess.Authorize(ctx.Request.Headers["X-Review-Token"].ToString(), student.ProfileId, SessionKey(ctx), key => IssuerAlive(state, key)));
    }
    static async Task<ElectronicsReviewDetail> Detail(ClassroomDb db, ReviewLease lease, string reportId)
    {
        var detail = await ElectronicsReviews.Detail(db, lease.StudentId, reportId);
        if (detail.Item.State != "awaiting-review" && !lease.OpenedReports.Contains(reportId)) throw new HttpFault(403, "В этом режиме доступны ожидающие проверки работы ученика.");
        lease.OpenedReports.Add(reportId); return detail;
    }
    public static void Map(RouteGroupBuilder api, ServerState state)
    {
        api.MapPost("/teacher/review-access", (HttpContext ctx, CreateReviewAccess input) =>
        {
            var teacher = state.Authorize(ctx, "teacher");
            return state.Read(async db =>
            {
                var student = await db.Profiles.SingleOrDefaultAsync(p => p.Id == input.StudentId && p.Role == "student") ?? throw new HttpFault(404, "Ученик не найден.");
                var code = state.ReviewAccess.Issue(student.Id, student.FullName ?? student.Login, teacher.ProfileId, BearerKey(ctx));
                return new ReviewAccessCode(code.Id, code.Code, code.Expires);
            });
        });
        api.MapDelete("/teacher/review-access/{id}", (HttpContext ctx, string id) =>
        {
            var teacher = state.Authorize(ctx, "teacher");
            return state.Read(_ => { state.ReviewAccess.Revoke(id, teacher.ProfileId); return Task.FromResult(new { ok = true }); });
        });
        api.MapPost("/review-access/redeem", (HttpContext ctx, RedeemReviewAccess input) =>
        {
            var student = state.Authorize(ctx, "student");
            if (input.Code.Length > 32) throw new HttpFault(400, "Введите код преподавателя.");
            var redeemed = Access(() => state.ReviewAccess.Redeem(input.Code, student.ProfileId, SessionKey(ctx), key => IssuerAlive(state, key)));
            return new ReviewAccessSession(redeemed.Token, student.ProfileId, redeemed.Lease.StudentName, redeemed.Lease.Expires);
        }).RequireRateLimiting("login");
        api.MapGet("/review-access/status", (HttpContext ctx) =>
        {
            var lease = Authorize(ctx, state); return new ReviewAccessStatus(lease.StudentId, lease.StudentName, lease.Expires);
        });
        api.MapDelete("/review-access", async (HttpContext ctx) =>
        {
            Authorize(ctx, state);
            return await state.Read(_ => { Authorize(ctx, state); state.ReviewAccess.End(ctx.Request.Headers["X-Review-Token"].ToString()); return Task.FromResult(new { ok = true }); });
        });
        api.MapGet("/review-access/electronics", (HttpContext ctx, int? offset) =>
        {
            Authorize(ctx, state); return state.Read(db => ElectronicsReviews.Queue(db, Authorize(ctx, state).StudentId, offset: offset ?? 0));
        });
        api.MapGet("/review-access/electronics/{studentId}/{reportId}", (HttpContext ctx, string studentId, string reportId) =>
        {
            var lease = Authorize(ctx, state);
            if (studentId != lease.StudentId) throw new HttpFault(403, "Код предназначен для другого ученика.");
            return state.Read(db => Detail(db, Authorize(ctx, state), reportId));
        });
        api.MapPut("/review-access/electronics/{studentId}/{reportId}/decisions/{id}", (HttpContext ctx, string studentId, string reportId, string id, ElectronicsReviewRequest input) =>
        {
            var lease = Authorize(ctx, state);
            if (studentId != lease.StudentId) throw new HttpFault(403, "Код предназначен для другого ученика.");
            return state.Write(async db => { var current = Authorize(ctx, state); await Detail(db, current, reportId); return await ElectronicsReviews.Decide(db, current.TeacherId, studentId, reportId, id, input); });
        });
    }
}
