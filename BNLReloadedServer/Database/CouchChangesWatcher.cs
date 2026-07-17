using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CouchDB.Driver;

namespace BNLReloadedServer.Database;

// Listens to CouchDB's `_changes?feed=continuous` stream and invokes a full-catalogue
// reload callback immediately whenever a document changes.
public class CouchChangesWatcher(string endpoint, string dbName, BasicCredentials credentials, Action onChanged)
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public void Start(CancellationToken cancellationToken) => _ = Task.Run(() => RunLoop(cancellationToken), cancellationToken);

    private async Task RunLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ListenOnce(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CouchWatcher] connection lost ({ex.Message}), retrying in 5s...");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ListenOnce(CancellationToken cancellationToken)
    {
        var url = $"{endpoint.TrimEnd('/')}/{dbName}/_changes?feed=continuous&since=now&heartbeat=30000";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credentials.Username}:{credentials.Password}")));

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        Console.WriteLine("[CouchWatcher] listening for catalogue changes...");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) return; // stream closed
            if (string.IsNullOrWhiteSpace(line)) continue; // heartbeat newline

            var docId = TryGetChangedDocId(line);
            Console.WriteLine(docId != null
                ? $"[CouchWatcher] card changed: {docId}, reloading catalogue..."
                : "[CouchWatcher] change detected, reloading catalogue...");

            try
            {
                onChanged();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CouchWatcher] reload failed: {ex.Message}");
            }
        }
    }

    private static string? TryGetChangedDocId(string changeLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(changeLine);
            return doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
