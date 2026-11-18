using ClaudeUsageServer.Services;

namespace ClaudeUsageServer.Tests;

public class UsageCacheTests
{
    [Fact]
    public void Empty_cache_has_neither_fresh_nor_stale_entry()
    {
        var cache = new UsageCache();

        Assert.False(cache.TryGetFresh(60, out _));
        Assert.False(cache.TryGetStale(out _));
    }

    [Fact]
    public void Stored_body_is_fresh_within_ttl()
    {
        var cache = new UsageCache();
        cache.Store("{\"a\":1}");

        Assert.True(cache.TryGetFresh(60, out var body));
        Assert.Equal("{\"a\":1}", body);
    }

    [Fact]
    public async Task Zero_ttl_disables_fresh_hits_but_keeps_stale_fallback()
    {
        var cache = new UsageCache();
        cache.Store("{\"a\":1}");
        await Task.Delay(20);

        Assert.False(cache.TryGetFresh(0, out _));
        Assert.True(cache.TryGetStale(out var stale));
        Assert.Equal("{\"a\":1}", stale);
    }

    [Fact]
    public void Latest_store_wins()
    {
        var cache = new UsageCache();
        cache.Store("old");
        cache.Store("new");

        Assert.True(cache.TryGetFresh(60, out var body));
        Assert.Equal("new", body);
    }
}
