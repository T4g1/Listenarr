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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Exceptions;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Services
{
    public class DownloadItemService(
        IConfigurationService configurationService,
        ILogger<IDownloadItemService> logger,
        IRemotePathMappingService remotePathMappingService,
        IDownloadClientGateway downloadClientGateway) : IDownloadItemService
    {

        public async Task<QueueItem> ResolveImportItemAsync(Download download, CancellationToken ct = default)
        {
            // Get the download client configuration
            var client = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
            if (client == null)
            {
                throw new InvalidOperationException($"Download {download.Id} references unknown download client {download.DownloadClientId}");
            }

            var queueItem = new QueueItem
            {
                Id = download.Id,
                Title = download.Title ?? "Unknown",
                Status = "completed",
                ContentPath = download.DownloadPath,
                DownloadClientId = client.Id
            };

            if (!client.IsEnabled)
            {
                logger.LogDebug($"Skipping import item resolution for download {download.Id}: download client {client.Name} ({client.Id}) is disabled");
                return queueItem;
            }

            logger.LogDebug($"Resolving import item for download {download.Id}");

            return await downloadClientGateway.GetImportItemAsync(
                client,
                download,
                queueItem,
                ct);
        }

        public async Task<List<string>> MatchLocalAndDownloadedFilesAsync(Download download, string localPath, CancellationToken cancellationToken = default)
        {
            var settings = await configurationService.GetApplicationSettingsAsync();

            var importableFiles = Directory.EnumerateFiles(localPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !FileUtils.IsBlacklistedFile(f, settings.ImportBlacklistExtensions))
                .Select(f => FileUtils.NormalizeStoredPath(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            try
            {
                var downloadClientItem = await ResolveImportItemAsync(download, cancellationToken);
                if (downloadClientItem == null || downloadClientItem.SourceFiles == null || downloadClientItem.SourceFiles.Count == 0)
                {
                    throw new DownloadProcessingException($"Unable to get the client item matching download or no files reported by the download client for download {download.Id}");
                }

                var allowedFiles = new HashSet<string>(
                    downloadClientItem.SourceFiles
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => FileUtils.NormalizeStoredPath(path)),
                    StringComparer.OrdinalIgnoreCase);

                // Apply remote path mapping
                var translationTasks = allowedFiles
                    .Select(path => remotePathMappingService.TranslatePathAsync(download.DownloadClientId, path));

                var tranlatedAllowedFiles = await Task.WhenAll(translationTasks);

                var filteredFiles = importableFiles
                    .Where(tranlatedAllowedFiles.Contains)
                    .ToList();

                if (filteredFiles.Count == 0)
                {
                    logger.LogWarning(
                        "Download client reported {ClientFileCount} related file(s) for download {DownloadId}, but none matched the local import candidates under {SourcePath}",
                        allowedFiles.Count,
                        download.Id,
                        localPath);
                }
                else
                {
                    logger.LogInformation(
                        "Scoped directory import for download {DownloadId} from {OriginalCount} to {FilteredCount} file(s) using the download client's reported file list",
                        download.Id,
                        importableFiles.Count,
                        filteredFiles.Count);
                }
                return filteredFiles;
            }
            catch (Exception ex) when (ex is not (DownloadProcessingException or OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                throw new DownloadProcessingException($"Unknown error while matching download client files to local files for import for download {download.Id}", ex);
            }
        }
    }
}
