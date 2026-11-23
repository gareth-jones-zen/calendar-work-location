using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using M365Graph;

namespace WorkLocation.Tests;

/// <summary>Graph stood in for: each request gets the next queued answer, and is recorded.</summary>
internal sealed class FakeGraph : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> answers = new();
    public List<string> Seen { get; } = [];

    public void Reply(HttpStatusCode status, string body = "{}") =>
        answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    public void Fail(Exception error) => answers.Enqueue(() => throw error);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        Seen.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
        return Task.FromResult(answers.Dequeue()());
    }

    public GraphCalendar Calendar() => new(new GraphClient(new GraphClientOptions { Log = _ => { } }, this,
        token: (_, _) => Task.FromResult("tok"), delay: (_, _) => Task.CompletedTask), TimeZoneInfo.Utc);
}

/// <summary>A write that may have reached Microsoft is never resent: a create is sent once and a 5xx or a lost answer
/// is "may have gone"; an update of the tool's own entry, and reads, still retry a 5xx.</summary>
public sealed class SendOnceTests
{
    private static readonly JsonObject Body = new()
    {
        ["subject"] = "Working from home",
        ["start"] = new JsonObject { ["dateTime"] = "2026-11-24T00:00:00", ["timeZone"] = "UTC" },
    };

    [Fact]
    public void A_create_that_gets_a_5xx_is_sent_once_and_may_have_gone()
    {
        var graph = new FakeGraph();
        graph.Reply(HttpStatusCode.ServiceUnavailable, "{\"error\": \"busy\"}");
        var error = Assert.Throws<GraphDeliveryUnknownException>(() => graph.Calendar().Create(Body));
        Assert.Equal(["POST /v1.0/me/events"], graph.Seen);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.Status);
    }

    [Fact]
    public void A_create_that_times_out_after_sending_is_sent_once_and_may_have_gone()
    {
        var graph = new FakeGraph();
        graph.Fail(new TaskCanceledException("timed out", new TimeoutException()));
        var error = Assert.Throws<GraphDeliveryUnknownException>(() => graph.Calendar().Create(Body));
        Assert.Single(graph.Seen);
        Assert.Null(error.Status);
    }

    [Fact]
    public void An_update_of_its_own_entry_retries_a_5xx()
    {
        var graph = new FakeGraph();
        graph.Reply(HttpStatusCode.BadGateway);
        graph.Reply(HttpStatusCode.OK);
        graph.Calendar().Update("abc", Body);
        Assert.Equal(["PATCH /v1.0/me/events/abc", "PATCH /v1.0/me/events/abc"], graph.Seen);
    }

    [Fact]
    public void Reading_the_calendar_retries_a_5xx()
    {
        var graph = new FakeGraph();
        graph.Reply(HttpStatusCode.ServiceUnavailable);
        graph.Reply(HttpStatusCode.OK, "{\"value\": []}");
        Assert.Empty(graph.Calendar().Events(new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 29)));
        Assert.Equal(2, graph.Seen.Count);
    }

    [Fact]
    public void A_delete_retries_a_5xx()
    {
        var graph = new FakeGraph();
        graph.Reply(HttpStatusCode.InternalServerError);
        graph.Reply(HttpStatusCode.NoContent, "");
        graph.Calendar().Delete("abc");
        Assert.Equal(["DELETE /v1.0/me/events/abc", "DELETE /v1.0/me/events/abc"], graph.Seen);
    }
}
