/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Responsabilities:
    /// - Make sure any path reported by any download client adapter is mapped using adequate Remote Path Mapping
    /// - Single point of contact for any download client adapter, no download client adapter detail should be visible behind this
    /// </summary>
    /// <param name="factory"></param>
    /// <param name="logger"></param>
    public class DownloadClientGateway(IDownloadClientAdapterFactory factory, ILogger<DownloadClientGateway> logger) : IDownloadClientGateway
    {
        private IDownloadClientAdapter ResolveAdapter(DownloadClientConfiguration client)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            var attemptedKeys = new List<string?> { client.Id, client.Type };
            foreach (var key in attemptedKeys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                try
                {
                    return factory.GetByIdOrType(key);
                }
                catch (InvalidOperationException)
                {
                    // Try the next key.
                    continue;
                }
            }

            var descriptor = !string.IsNullOrWhiteSpace(client.Name)
                ? $"{client.Name} ({client.Type ?? "unknown"})"
                : client.Type ?? client.Id ?? "unknown";

            var message = $"No download client adapter registered for {LogRedaction.SanitizeText(descriptor)}.";
            logger.LogError(message);
            throw new InvalidOperationException(message);
        }

        public Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.TestConnectionAsync(client, ct);
        }

        public Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.AddAsync(client, result, ct);
        }

        public Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.RemoveAsync(client, id, deleteFiles, ct);
        }

        public Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.GetQueueAsync(client, ct);
        }

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.GetRecentHistoryAsync(client, limit, ct);
        }

        public Task<bool> MarkItemAsImportedAsync(DownloadClientConfiguration client, string downloadId, CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.MarkItemAsImportedAsync(client, downloadId, ct);
        }

        public Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            CancellationToken ct = default)
        {
            var adapter = ResolveAdapter(client);
            return adapter.GetImportItemAsync(client, download, queueItem, null, ct);
        }

        // TODO: Apply path mapping on all file paths
    }
}
