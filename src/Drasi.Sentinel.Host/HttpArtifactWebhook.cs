using System.Net.Http.Headers;
using System.Text;
using Drasi.Sentinel.Application;

namespace Drasi.Sentinel.Host;

public sealed class HttpArtifactWebhook : IArtifactWebhook, IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _url;

    public HttpArtifactWebhook(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        _url = url;
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task DeliverAsync(string artifactJson, CancellationToken cancellationToken)
    {
        using var content = new StringContent(artifactJson, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _client.PostAsync(_url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}
