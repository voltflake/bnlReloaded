using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BNLReloadedServer.BaseTypes;
using BNLReloadedServer.ProtocolHelpers;
using BNLReloadedServer.ServerTypes;
using CouchDB.Driver;

namespace BNLReloadedServer.Database;

public class CouchCatalogueStore(
    CouchClient fromDb, 
    string dbName,
    string toPath,
    string deserializedPath,
    JsonSerializerOptions serializerOptions): CatalogueStore
{
    private static readonly HttpClient _httpClient = new();

    private class AllDocsResponse
    {
        [JsonPropertyName("rows")]
        public List<DocRow> Rows { get; set; } = [];
    }

    private class DocRow
    {
        [JsonPropertyName("doc")]
        public JsonElement Doc { get; set; }
    }

    public override void Store(IEnumerable<Card> cards)
    {
        using var fs = new StreamWriter(File.Create(toPath));
        fs.Write(JsonSerializer.Serialize(cards, serializerOptions).Replace("\\u00A0", "\u00A0"));
    }

    public override List<Card> Load(IEnumerable<CardMap> maps, ExtraMaps? extraMaps)
    {
        var url = $"{fromDb.Endpoint.OriginalString.TrimEnd('/')}/{dbName}/_all_docs?include_docs=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var creds = Databases.ConfigDatabase.CouchDbCredentials();
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes($"{creds.Username}:{creds.Password}")));

        var response = _httpClient.SendAsync(request).Result;
        response.EnsureSuccessStatusCode();
        var allDocs = response.Content.ReadFromJsonAsync<AllDocsResponse>().Result!;

        List<Card> cards = [];
        foreach (var row in allDocs.Rows)
        {
            if (!row.Doc.TryGetProperty("category", out _)) continue;
            var card = JsonSerializer.Deserialize<Card>(row.Doc.GetRawText(), serializerOptions);
            if (card != null) cards.Add(card);
        }

        Console.WriteLine($"Loaded {cards.Count} cards from remote database");

        // Add maps
        AddMaps(cards, maps, extraMaps);

        var memStream = new MemoryStream();
        var writer = new BinaryWriter(memStream);
        using var fs2 = File.Create(deserializedPath);
        writer.Write((byte)0);
        writer.WriteList(cards, Card.WriteVariant);
        var zipped = (writer.BaseStream as MemoryStream)?.GetBuffer().Zip(0);
        zipped?.CopyTo(fs2);
        zipped?.Close();

        return cards;
    }
}