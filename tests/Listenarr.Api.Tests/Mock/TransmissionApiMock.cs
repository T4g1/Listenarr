using System.Text.Json;
using Listenarr.Api.Tests;

public class TransmissionApiMock : BaseMock
{
    public TransmissionApiMock()
    {
        AddRoute("transmission/rpc", GetTorrent, HttpMethod.Post);
    }

    public async Task<HttpResponseMessage> GetTorrent(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("method", out JsonElement methoPostElement))
        {
            string method = methoPostElement.GetString();
            if (string.Equals("torrent-get", method, StringComparison.OrdinalIgnoreCase))
            {
                return MockUtils.GetCannedResponse("""
                {
                    "arguments": {
                        "torrents": [
                            {
                                "downloadDir": "/downloads/complete/audiobooks",
                                "id": 306,
                                "name": "Isaac Asimov - Le Cycle de Fondation - Tome 3 - Seconde Fondation "
                            }
                        ]
                    },
                    "result": "success",
                    "tag": 3
                }
                """);
            }
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }
}
