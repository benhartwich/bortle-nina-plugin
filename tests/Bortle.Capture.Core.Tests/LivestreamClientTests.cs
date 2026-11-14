using System.Net;
using System.Text;
using Xunit;

namespace Bortle.Capture.Tests;

public sealed class LivestreamClientTests
{
    private sealed class Answer : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string? Auth)> Calls = new();
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = "{}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (BortleClient, Answer) Make()
    {
        var answer = new Answer();
        var client = new BortleClient(
            new HttpClient(answer), () => new ClientSettings("https://bortle.example/", "bnp_secret"));
        return (client, answer);
    }

    [Fact]
    public async Task Going_live_asks_the_server_with_the_plugin_key()
    {
        var (client, answer) = Make();
        answer.Body = "{\"allowed\":true,\"configured\":true,\"state\":\"live\","
            + "\"showing\":{\"target\":\"IC 405\",\"frames\":3,\"integration_s\":900}}";
        var result = await client.StartLivestreamAsync(CancellationToken.None);

        Assert.Equal(Outcome.Ok, result.Outcome);
        Assert.True(result.Value!.IsLive);
        Assert.Equal("IC 405", result.Value.Showing!.Target);
        Assert.Equal(900, result.Value.Showing.IntegrationSeconds);
        Assert.Equal(("POST", "/capture/livestream/start", "Bearer bnp_secret"), answer.Calls[0]);
    }

    [Fact]
    public async Task Without_a_stream_key_the_server_says_what_to_do()
    {
        var (client, answer) = Make();
        answer.Status = HttpStatusCode.Conflict;
        answer.Body = "{\"detail\":\"Noch kein Stream-Schlüssel: im Cockpit unter Profil → Livestream eintragen.\"}";
        var result = await client.StartLivestreamAsync(CancellationToken.None);

        Assert.NotEqual(Outcome.Ok, result.Outcome);
        Assert.Contains("Profil → Livestream", result.Message);
    }

    [Fact]
    public async Task Status_and_stop_use_their_own_calls()
    {
        var (client, answer) = Make();
        answer.Body = "{\"allowed\":true,\"configured\":true,\"state\":\"off\"}";
        Assert.False((await client.LivestreamAsync(CancellationToken.None)).Value!.IsLive);
        await client.StopLivestreamAsync(CancellationToken.None);
        Assert.Equal(("GET", "/capture/livestream"), (answer.Calls[0].Method, answer.Calls[0].Path));
        Assert.Equal(("POST", "/capture/livestream/stop"), (answer.Calls[1].Method, answer.Calls[1].Path));
    }
}
