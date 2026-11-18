using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeUsageServer.Tests;

/// <summary>A request as seen by <see cref="FakeHttpHandler"/>, with its body already read.</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

/// <summary>
/// Stub HTTP handler: records every request and answers via <c>respond</c>. Never touches
/// the network, so no test can reach Anthropic.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();
    private readonly Func<CapturedRequest, Task<HttpResponseMessage>> _respond;

    public FakeHttpHandler(Func<CapturedRequest, HttpResponseMessage> respond)
        => _respond = request => Task.FromResult(respond(request));

    private FakeHttpHandler(Func<CapturedRequest, Task<HttpResponseMessage>> respond)
        => _respond = respond;

    /// <summary>A handler whose responses can be delayed, e.g. to hold a request in flight.</summary>
    public static FakeHttpHandler Async(Func<CapturedRequest, Task<HttpResponseMessage>> respond) => new(respond);

    public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(
            h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers, body);
        _requests.Enqueue(captured);
        return await _respond(captured);
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Captures every formatted log message (and exception text) at all levels.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => _messages.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _messages.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}

internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until it holds or the timeout elapses.</summary>
    public static async Task UntilAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting until " + because);
            await Task.Delay(25);
        }
    }
}
