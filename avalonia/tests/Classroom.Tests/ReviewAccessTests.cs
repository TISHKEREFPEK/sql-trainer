using Classroom.Domain;
using Xunit;

namespace Classroom.Tests;

public sealed class ReviewAccessTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Current = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Current;
    }
    [Fact]
    public void AccessIsSingleUseAndBoundToPupilDeviceAndLiveIssuer()
    {
        var registry = new ReviewAccessRegistry();
        var code = registry.Issue("alice", "Ученица", "teacher", "issuer");
        Assert.Throws<ReviewAccessException>(() => registry.Redeem(code.Code, "bob", "other-device", _ => true));
        var redeemed = registry.Redeem(code.Code, "alice", "device", _ => true);
        Assert.Throws<ReviewAccessException>(() => registry.Redeem(code.Code, "alice", "device", _ => true));
        Assert.Equal("alice", registry.Authorize(redeemed.Token, "alice", "device", key => key == "issuer").StudentId);
        Assert.Throws<ReviewAccessException>(() => registry.Authorize(redeemed.Token, "alice", "new-login", _ => true));
        Assert.Throws<ReviewAccessException>(() => registry.Authorize(redeemed.Token, "bob", "device", _ => true));
        Assert.Throws<ReviewAccessException>(() => registry.Authorize(redeemed.Token, "alice", "device", _ => false));
        registry.Revoke(code.Id, "other-teacher");
        registry.Authorize(redeemed.Token, "alice", "device", _ => true);
        registry.Revoke(code.Id, "teacher");
        Assert.Throws<ReviewAccessException>(() => registry.Authorize(redeemed.Token, "alice", "device", _ => true));
    }
    [Fact]
    public void ExpiryThrottlingReplacementAndClearCloseAccess()
    {
        var clock = new Clock(); var registry = new ReviewAccessRegistry(clock);
        var code = registry.Issue("alice", "Ученица", "teacher", "issuer");
        for (var i = 0; i < 5; i++) Assert.Throws<ReviewAccessException>(() => registry.Redeem("not-a-code", "alice", "device", _ => true));
        Assert.True(Assert.Throws<ReviewAccessException>(() => registry.Redeem(code.Code, "alice", "device", _ => true)).RateLimited);
        clock.Current += TimeSpan.FromMinutes(1.1);
        var redeemed = registry.Redeem(code.Code, "alice", "device", _ => true);
        clock.Current += TimeSpan.FromMinutes(15);
        Assert.Throws<ReviewAccessException>(() => registry.Authorize(redeemed.Token, "alice", "device", _ => true));
        var expired = registry.Issue("alice", "Ученица", "teacher", "issuer"); clock.Current += TimeSpan.FromMinutes(5);
        Assert.Throws<ReviewAccessException>(() => registry.Redeem(expired.Code, "alice", "device", _ => true));
        var first = registry.Issue("alice", "Ученица", "teacher", "issuer");
        var second = registry.Issue("alice", "Ученица", "teacher", "issuer");
        Assert.Throws<ReviewAccessException>(() => registry.Redeem(first.Code, "alice", "device", _ => true));
        var lease = registry.Redeem(second.Code, "alice", "device", _ => true);
        registry.Issue("alice", "Ученица", "teacher", "issuer");
        Assert.Throws<ReviewAccessException>(() => registry.Authorize(lease.Token, "alice", "device", _ => true));
        var active = registry.Issue("alice", "Ученица", "teacher", "issuer");
        var activeLease = registry.Redeem(active.Code, "alice", "device", _ => true);
        var unused = registry.Issue("bob", "Ученик", "teacher", "issuer");
        registry.Clear();
        Assert.Throws<ReviewAccessException>(() => registry.Authorize(activeLease.Token, "alice", "device", _ => true));
        Assert.Throws<ReviewAccessException>(() => registry.Redeem(unused.Code, "bob", "other-device", _ => true));
    }
    [Fact]
    public async Task ConcurrentRedemptionGrantsExactlyOneSession()
    {
        var registry = new ReviewAccessRegistry(); var code = registry.Issue("alice", "Ученица", "teacher", "issuer");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            try { registry.Redeem(code.Code, "alice", "device-" + i, _ => true); return true; }
            catch (ReviewAccessException) { return false; }
        })));
        Assert.Single(results, r => r);
    }
}
