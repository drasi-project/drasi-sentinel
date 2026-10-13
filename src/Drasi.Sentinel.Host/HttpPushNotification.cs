using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drasi.Sentinel.Application;
using Drasi.Sentinel.Domain;

namespace Drasi.Sentinel.Host;

public sealed class HttpPushNotification : IPushNotificationSender, IDisposable
{
    private readonly HttpClient _client;

    public HttpPushNotification()
    {
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public static string ArtifactUpdateJson(ConversationEvent observation)
    {
        JsonNode? data;
        try
        {
            data = JsonNode.Parse(observation.Content);
        }
        catch (JsonException)
        {
            data = observation.Content;
        }

        return JsonSerializer.Serialize(new
        {
            artifactUpdate = new
            {
                taskId = observation.TaskId.Value,
                contextId = observation.TaskId.Value,
                artifact = new
                {
                    artifactId = observation.Sequence.Value.ToString(CultureInfo.InvariantCulture),
                    parts = new[] { new { data } },
                },
                append = false,
                lastChunk = true,
            },
        });
    }

    public async Task DeliverAsync(PushNotificationConfig config, ConversationEvent observation, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.Url)
        {
            Content = new StringContent(ArtifactUpdateJson(observation), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(config.Token))
            request.Headers.TryAddWithoutValidation("X-A2A-Notification-Token", config.Token);
        if (!string.IsNullOrEmpty(config.AuthScheme) && !string.IsNullOrEmpty(config.AuthCredentials))
            request.Headers.Authorization = new AuthenticationHeaderValue(config.AuthScheme, config.AuthCredentials);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}
