namespace Listenarr.Api.Models
{
    public class DownloadDto
    {
        public string Id { get; set; } = string.Empty;
        public int AudiobookId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string OriginalUrl { get; set; } = string.Empty;
        public DownloadStatus Status { get; set; }
        public decimal Progress { get; set; }
        public long TotalSize { get; set; }
        public long DownloadedSize { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public string DownloadClientId { get; set; } = string.Empty;
        public string DownloadClientName { get; set; } = string.Empty;
        public Dictionary<string, object> Metadata { get; set; } = [];
        public string? ImportBlockReason { get; set; }
        public List<string>? ImportBlockMessages { get; set; }
        public int ImportAttempts { get; set; }

        public static DownloadDto CreateFrom(Download download, DownloadClientConfiguration? client)
        {
            if (download.AudiobookId == null)
            {
                throw new ArgumentException($"Inconsistency detected: Download {download.Id} is not linked to any audio book and should be removed");
            }

            object? sanitizedMetadata = null;
            if (download.Metadata != null)
            {
                var dict = new Dictionary<string, object>();
                foreach (var kvp in download.Metadata.Where(kvp => !string.Equals(kvp.Key, "ClientContentPath", StringComparison.OrdinalIgnoreCase)))
                {
                    dict[kvp.Key] = kvp.Value!;
                }
                sanitizedMetadata = dict;
            }

            var downloadDto = new DownloadDto
            {
                Id = download.Id,
                AudiobookId = download.AudiobookId.GetValueOrDefault(),
                Title = download.Title,
                Artist = download.Artist,
                Album = download.Album,
                OriginalUrl = download.OriginalUrl,
                Status = download.Status,
                Progress = download.Progress,
                TotalSize = download.TotalSize,
                DownloadedSize = download.DownloadedSize,
                StartedAt = download.StartedAt,
                CompletedAt = download.CompletedAt,
                ErrorMessage = download.ErrorMessage,
                DownloadClientId = download.DownloadClientId,
                Metadata = download.GetFilteredMetadata(),
                ImportBlockReason = download.ImportBlockReason,
                ImportBlockMessages = download.ImportBlockMessages,
                ImportAttempts = download.ImportAttempts
            };

            if (client != null)
            {
                downloadDto.DownloadClientName = client.Name;
            }

            return downloadDto;
        }
    }
}
