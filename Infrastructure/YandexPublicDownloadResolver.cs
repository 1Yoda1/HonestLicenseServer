using System.Net.Http.Json;

namespace HonestLicenseServer.Infrastructure;

public interface IYandexPublicDownloadResolver
{
    Task<string?> ResolveAsync(string publicKey, string path,
        CancellationToken cancellationToken = default);
}

public sealed class YandexPublicDownloadResolver(HttpClient httpClient)
    : IYandexPublicDownloadResolver
{
    private const string DownloadEndpoint =
        "https://cloud-api.yandex.net/v1/disk/public/resources/download";

    public async Task<string?> ResolveAsync(string publicKey, string path,
        CancellationToken cancellationToken = default)
    {
        var url = $"{DownloadEndpoint}?public_key={Uri.EscapeDataString(publicKey)}" +
            $"&path={Uri.EscapeDataString(path)}";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<YandexDownloadResponse>(
            cancellationToken: cancellationToken);
        return Uri.TryCreate(value?.Href, UriKind.Absolute, out var href) &&
            href.Scheme == Uri.UriSchemeHttps ? href.ToString() : null;
    }

    private sealed record YandexDownloadResponse(string? Href);
}
