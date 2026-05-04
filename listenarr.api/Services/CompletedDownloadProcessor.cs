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
using System.Text.Json;
using Listenarr.Domain.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Models;

namespace Listenarr.Api.Services
{
    public class CompletedDownloadProcessor(
        IDownloadRepository downloadRepository,
        IConfigurationService configurationService,
        IDownloadImportService downloadImportService,
        IArchiveExtractor archiveExtractor,
        IDownloadQueueService downloadQueueService,
        ILogger<CompletedDownloadProcessor> logger,
        IHubBroadcaster hubBroadcaster,
        IDownloadHistoryService downloadHistoryService,
        IAudiobookRepository audiobookRepository,
        IToastService toastService,
        IDownloadClientGateway downloadClientGateway,
        INotificationService notificationService,
        IHistoryRepository historyRepository,
        IDownloadItemService downloadItemService) : ICompletedDownloadProcessor
    {
        public async Task ProcessCompletedDownloadAsync(Download download, string finalPath)
        {
            logger.LogInformation($"ProcessCompletedDownloadAsync called for {download.Id} (finalPath: {finalPath})");

            if (download.AudiobookId == null)
            {
                // FIXME: Remove the download ?
                throw new InvalidOperationException($"Download {download.Id} is inconsistent: No audiobook related to it");
            }

            if (download.DownloadClientId == null)
            {
                // FIXME: Remove the download ?
                throw new InvalidOperationException($"Download {download.Id} is inconsistent: No download client related to it");
            }

            var audiobook = await audiobookRepository.GetByIdAsync((int)download.AudiobookId);
            if (audiobook == null)
            {
                throw new InvalidOperationException($"Download {download.Id}'s audiobook {download.AudiobookId} cannot be retrieved");
            }

            var client = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
            if (client == null)
            {
                throw new InvalidOperationException($"Download {download.Id}'s client {download.DownloadClientId} cannot be retrieved");
            }

            // Check download status
            try
            {
                await downloadRepository.UpdateAsync(download.Importing());
                logger.LogInformation($"Marked download {download.Id} as ImportPending (pre-import)");
            }
            catch (InvalidOperationException exception)
            {
                logger.LogError($"Download status cannot be changed: {exception.Message}");
                return;
            }

            try
            {
                try
                {
                    await Task.Delay(100); // Brief delay for DB commit
                    var queueAfterComplete = await downloadQueueService.GetQueueSnapshotAsync();
                    await hubBroadcaster.BroadcastQueueUpdateAsync(queueAfterComplete);
                    logger.LogDebug("Broadcasted QueueUpdate after updating download {DownloadId}", download.Id);
                }
                catch (Exception broadcastEx) when (broadcastEx is not OperationCanceledException && broadcastEx is not OutOfMemoryException && broadcastEx is not StackOverflowException)
                {
                    logger.LogDebug(broadcastEx, "Failed to broadcast after updating Download");
                }

                var importToastSent = false;
                var settings = await configurationService.GetApplicationSettingsAsync();

                // TODO: What the fuck is happening here ?
                var importPath = ResolveCompletedImportPath(finalPath, settings.ImportBlacklistExtensions);

                if (string.IsNullOrWhiteSpace(importPath))
                {
                    logger.LogWarning("ProcessCompletedDownloadAsync: finalPath is empty for download {DownloadId}", download.Id);
                }
                else
                {
                    var sourceDirectory = finalPath;
                    if (!Directory.Exists(finalPath))
                    {
                        sourceDirectory = Path.GetDirectoryName(finalPath);
                    }

                    if (string.IsNullOrEmpty(sourceDirectory))
                    {
                        throw new InvalidOperationException($"Unable to get directory from {finalPath}");
                    }

                    var files = await downloadItemService.MatchLocalAndDownloadedFilesAsync(download, sourceDirectory);

                    // Filter archives
                    var archives = files.Where(archiveExtractor.IsArchive).ToList();
                    files = [.. files.Where(f => !archiveExtractor.IsArchive(f))];

                    List<ImportResult> importResults = [];
                    if (files.Count > 0)
                    {
                        importResults = await downloadImportService.ImportFilesFromDirectoryAsync(download, audiobook, files, settings);
                        logger.LogInformation("FileFinalizer.ImportFilesFromDirectoryAsync returned {Count} results for download {DownloadId}", importResults, download.Id);
                    }

                    // Process archives inside the directory (extract and import)
                    if (settings.ExtractArchives)
                    {
                        foreach (var archive in archives)
                        {
                            using var tempDirectory = await archiveExtractor.ExtractArchiveToTempDirAsync(archive);
                            if (tempDirectory != null)
                            {
                                var tempDirExtracted = tempDirectory.Path;
                                var extractedFiles = System.IO.Directory.GetFiles(tempDirExtracted, "*", System.IO.SearchOption.AllDirectories)
                                    .Where(f => !FileUtils.IsBlacklistedFile(f, settings.ImportBlacklistExtensions))
                                    .ToArray();
                                if (extractedFiles != null && extractedFiles.Length > 0)
                                {
                                    var extractedResults = await downloadImportService.ImportFilesFromDirectoryAsync(download, audiobook, extractedFiles, settings);
                                    logger.LogInformation("Imported {Count} files extracted from archive {Archive} for download {DownloadId}", extractedResults.Count, archive, download.Id);

                                    importResults.AddRange(extractedResults);
                                }
                            }
                        }
                    }

                    var destinationPath = SelectPrimaryImportedPath(importResults);

                    await downloadRepository.UpdateAsync(download.Imported());
                    logger.LogInformation("Updated download {DownloadId}: {FinalPath}", download.Id, destinationPath);

                    // Record successful import in history for idempotency
                    if (downloadHistoryService != null && !string.IsNullOrEmpty(download.DownloadClientId))
                    {
                        await downloadHistoryService.RecordImportedAsync(
                            download.Id,
                            download.DownloadClientId,
                            download.Title ?? "Unknown",
                            audiobookId: null);  // Audiobook ID is int in Download, but Guid in DownloadHistory (FIXME: That does not explain why we set this to null)
                        logger.LogInformation("Recorded successful import in history for download {DownloadId}", download.Id);
                    }

                    // Send notification
                    try
                    {
                        var webhooks = await configurationService.GetWebhookConfigurationsAsync();
                        foreach (var webhook in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Imported")))
                        {
                            await notificationService.SendNotificationAsync(
                                "Imported",
                                new
                                {
                                    AudiobookTitle = download.Title,
                                    DownloadClient = client.Name,
                                    FilePath = audiobook.BasePath,
                                    Timestamp = DateTime.UtcNow
                                },
                                webhook.Url,
                                webhook.Triggers
                            );
                        }
                    }
                    catch (Exception notifyEx) when (notifyEx is not OperationCanceledException && notifyEx is not OutOfMemoryException && notifyEx is not StackOverflowException)
                    {
                        logger.LogWarning(notifyEx, "Failed to send import notification for {DownloadId}", download.Id);
                    }

                    // Send toast notification for successful import
                    var downloadName = !string.IsNullOrEmpty(download.Title) ? download.Title : "Download";
                    var message = $"{downloadName} has been imported into {audiobook.Title}";

                    if (!importToastSent)
                    {
                        await toastService.PublishToastAsync(
                            "success",
                            "Import Complete",
                            message,
                            timeoutMs: 5000);
                        importToastSent = true;
                        logger.LogDebug("Sent toast notification for imported download {DownloadId}", download.Id);
                    }
                }

                if (download.Status == DownloadStatus.ImportPending)
                {
                    await MarkImportFailureAsync(
                        download,
                        "NoImportableFiles",
                        "Import was not successful (possible quality rejection or duplicate). Manual interaction is required.",
                        forceBlock: true);
                }

                // Cleanup from download client if configured
                // FIXME: Move that into download client adapters
                try
                {
                    // Reload download to ensure it wasn't deleted by concurrent operations
                    var downloadForCleanup = await downloadRepository.FindAsync(download.Id);

                    logger.LogDebug("Cleanup section: download is {IsNull}, DownloadClientId={ClientId}",
                        downloadForCleanup == null ? "NULL" : "NOT NULL",
                        downloadForCleanup?.DownloadClientId ?? "NULL");

                    if (downloadForCleanup != null && !string.IsNullOrWhiteSpace(downloadForCleanup.DownloadClientId))
                    {
                        logger.LogInformation("Cleanup: RemoveCompletedDownloads={Setting}",
                            client.RemoveCompletedDownloads ?? "NULL");

                        // Skip cleanup if the download client is disabled
                        if (client.IsEnabled)
                        {
                            logger.LogDebug("Skipping post-import cleanup for download {DownloadId}: client {ClientName} is disabled",
                                downloadForCleanup.Id, client.Name);
                        }
                        else if (!string.IsNullOrEmpty(client.RemoveCompletedDownloads) &&
                            client.RemoveCompletedDownloads != "none")
                        {
                            // Sonarr parity: Mark item as imported (e.g., change torrent category) before removal.
                            // This ensures the torrent is properly categorized even if removal is deferred.
                            string clientIdForMark = downloadForCleanup.Id;
                            if (downloadForCleanup.Metadata != null && downloadForCleanup.Metadata.TryGetValue("TorrentHash", out var markHashObj))
                            {
                                var markHash = markHashObj?.ToString();
                                if (!string.IsNullOrEmpty(markHash))
                                    clientIdForMark = markHash;
                            }
                            try
                            {
                                await downloadClientGateway.MarkItemAsImportedAsync(client, clientIdForMark);
                            }
                            catch (Exception markEx) when (markEx is not OperationCanceledException && markEx is not OutOfMemoryException && markEx is not StackOverflowException)
                            {
                                logger.LogDebug(markEx, "Failed to mark download {DownloadId} as imported in client (non-fatal)", downloadForCleanup.Id);
                            }

                            // Sonarr parity: Check CanBeRemoved flag before attempting removal.
                            // If the torrent hasn't reached its seed limit, defer removal to the next cycle.
                            bool canBeRemoved = true; // Default true for usenet clients
                            if (downloadForCleanup.Metadata != null && downloadForCleanup.Metadata.TryGetValue("CanBeRemoved", out var canRemoveObj))
                            {
                                canBeRemoved = canRemoveObj is bool b ? b : (canRemoveObj is System.Text.Json.JsonElement je ? je.GetBoolean() : bool.TryParse(canRemoveObj?.ToString(), out var parsed) && parsed);
                            }

                            if (!canBeRemoved)
                            {
                                logger.LogInformation("Download {DownloadId} cannot be removed yet (CanBeRemoved=false, torrent still seeding). Deferring removal to next cycle.",
                                    downloadForCleanup.Id);
                                // Don't remove - let the monitor service update CanBeRemoved on the next poll
                                // when the torrent eventually reaches its seed limit
                            }
                            else
                            {
                                bool deleteFiles = client.RemoveCompletedDownloads == "remove_and_delete";

                                // Get the actual client-specific ID (torrent hash for qBittorrent/Transmission, droneId for NZBGet, etc.)
                                string clientId = downloadForCleanup.Id;

                                if ((client.Type.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase) ||
                                        client.Type.Equals("transmission", StringComparison.OrdinalIgnoreCase)) &&
                                    downloadForCleanup.Metadata != null && downloadForCleanup.Metadata.TryGetValue("TorrentHash", out var hashObj))
                                {
                                    var torrentHash = hashObj?.ToString();
                                    if (!string.IsNullOrEmpty(torrentHash))
                                    {
                                        clientId = torrentHash;
                                        logger.LogDebug("Using torrent hash {Hash} instead of download ID for {ClientType} removal", torrentHash, client.Type);
                                    }
                                }
                                else if (client.Type.Equals("nzbget", StringComparison.OrdinalIgnoreCase) &&
                                            downloadForCleanup.Metadata != null && downloadForCleanup.Metadata.TryGetValue("TorrentHash", out var droneIdObj))
                                {
                                    // For NZBGet, TorrentHash actually contains the droneId (GUID)
                                    var droneId = droneIdObj?.ToString();
                                    if (!string.IsNullOrEmpty(droneId))
                                    {
                                        clientId = droneId;
                                        logger.LogDebug("Using droneId {DroneId} instead of download ID for NZBGet removal", droneId);
                                    }
                                }
                                else if (client.Type.Equals("sabnzbd", StringComparison.OrdinalIgnoreCase) &&
                                            downloadForCleanup.Metadata != null && downloadForCleanup.Metadata.TryGetValue("ClientDownloadId", out var sabIdObj))
                                {
                                    var sabId = sabIdObj?.ToString();
                                    if (!string.IsNullOrEmpty(sabId))
                                    {
                                        clientId = sabId;
                                        logger.LogDebug("Using ClientDownloadId {NzoId} instead of download ID for SABnzbd removal", sabId);
                                    }
                                }

                                var removed = await downloadClientGateway.RemoveAsync(client, clientId, deleteFiles);

                                if (removed)
                                {
                                    logger.LogInformation("Removed download {DownloadId} from client {ClientName} (deleteFiles={DeleteFiles})",
                                        downloadForCleanup.Id, client.Name, deleteFiles);

                                    // Log to history
                                    var historyEntry = new History
                                    {
                                        AudiobookId = downloadForCleanup.AudiobookId,
                                        AudiobookTitle = downloadForCleanup.Title,
                                        EventType = "Imported",
                                        Message = $"Automatically imported and removed from {client.Name}. Files deleted: {deleteFiles}",
                                        Source = "AutoImport",
                                        Timestamp = DateTime.UtcNow,
                                        NotificationSent = false,
                                        Data = System.Text.Json.JsonSerializer.Serialize(new
                                        {
                                            DownloadId = downloadForCleanup.Id,
                                            ClientName = client.Name,
                                            ClientType = client.Type,
                                            FilesDeleted = deleteFiles,
                                            FinalPath = downloadForCleanup.FinalPath
                                        })
                                    };
                                    await historyRepository.AddAsync(historyEntry);
                                    logger.LogInformation("Added history entry for automatic import of {DownloadId}", download.Id);

                                    // Send notification
                                    try
                                    {
                                        var webhooks = await configurationService.GetWebhookConfigurationsAsync();
                                        foreach (var webhook in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Imported")))
                                        {
                                            await notificationService.SendNotificationAsync(
                                                "Imported",
                                                new
                                                {
                                                    AudiobookTitle = downloadForCleanup.Title,
                                                    DownloadClient = client.Name,
                                                    FilePath = downloadForCleanup.FinalPath,
                                                    RemovedFromClient = true,
                                                    FilesDeleted = deleteFiles,
                                                    Timestamp = DateTime.UtcNow
                                                },
                                                webhook.Url,
                                                webhook.Triggers
                                            );
                                        }

                                        // Mark notification as sent
                                        historyEntry.NotificationSent = true;
                                        await historyRepository.UpdateAsync(historyEntry);
                                    }
                                    catch (Exception notifyEx) when (notifyEx is not OperationCanceledException && notifyEx is not OutOfMemoryException && notifyEx is not StackOverflowException)
                                    {
                                        logger.LogWarning(notifyEx, "Failed to send import notification for {DownloadId}", download.Id);
                                    }

                                    // Send toast notification for successful import
                                    try
                                    {
                                        var downloadName = !string.IsNullOrEmpty(downloadForCleanup.Title) ? downloadForCleanup.Title : "Download";
                                        var message = client.RemoveCompletedDownloads == "remove_and_delete"
                                            ? $"{downloadName} has been imported into {audiobook.Title} and files deleted"
                                            : $"{downloadName} has been imported into {audiobook.Title}";

                                        if (!importToastSent)
                                        {
                                            await toastService.PublishToastAsync(
                                                "success",
                                                "Import Complete",
                                                message,
                                                timeoutMs: 5000); // Auto-dismiss after 5 seconds
                                            logger.LogDebug("Sent toast notification for imported download {DownloadId}", download.Id);
                                        }
                                    }
                                    catch (Exception toastEx) when (toastEx is not OperationCanceledException && toastEx is not OutOfMemoryException && toastEx is not StackOverflowException)
                                    {
                                        logger.LogDebug(toastEx, "Failed to send toast notification for {DownloadId}", download.Id);
                                    }

                                    // Delete the download record from database after successful cleanup
                                    try
                                    {
                                        await downloadRepository.RemoveAsync(download.Id);
                                        logger.LogInformation("Deleted download {DownloadId} from database after successful cleanup", download.Id);

                                        // Small delay to ensure database changes are visible to other contexts
                                        await Task.Delay(100);
                                    }
                                    catch (Exception deleteEx) when (deleteEx is not OperationCanceledException && deleteEx is not OutOfMemoryException && deleteEx is not StackOverflowException)
                                    {
                                        logger.LogWarning(deleteEx, "Failed to delete download {DownloadId} from database", download.Id);
                                    }
                                }
                                else
                                {
                                    logger.LogWarning("Failed to remove download {DownloadId} from client {ClientName}",
                                        download!.Id, client.Name);
                                }
                            }
                        }
                    }
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogError(exception, "Error during post-import cleanup for {DownloadId}", download.Id);
                }

                // FIXME: Should be done elsewhere
                // Broadcast queue updates
                try
                {
                    var currentQueue = await downloadQueueService.GetQueueSnapshotAsync();
                    await hubBroadcaster.BroadcastQueueUpdateAsync(currentQueue);
                    logger.LogInformation("Broadcasted QueueUpdate via IHubBroadcaster after processing download {DownloadId}", download.Id);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogWarning(exception, "Failed to broadcast QueueUpdate after processing download {DownloadId}", download.Id);
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogError(exception, "Unexpected error in ProcessCompletedDownloadAsync for {DownloadId}", download.Id);

                await MarkImportFailureAsync(
                    download,
                    "UnhandledImportError",
                    exception.Message ?? "Unexpected import processing error",
                    exception,
                    forceBlock: false);
            }
        }

        private async Task MarkImportFailureAsync(
            Download download,
            string reason,
            string message,
            Exception? exception = null,
            bool forceBlock = false)
        {
            if (forceBlock)
            {
                await downloadRepository.UpdateAsync(download.HardBlocked(reason, message));
            }
            else
            {
                await downloadRepository.UpdateAsync(download.Blocked(reason, message));
            }

            var detail = message;
            if (exception != null && exception.InnerException != null)
            {
                detail += $" | Inner: {exception.InnerException.Message}";
            }

            await downloadHistoryService.RecordImportFailedAsync(
                download.Id,
                download.DownloadClientId,
                download.Title ?? "Unknown",
                detail);

            if (download.IsBlocked())
            {
                logger.LogWarning(
                    "Download {DownloadId} import blocked (Reason: {Reason}, Attempts: {Attempts})",
                    download.Id,
                    reason,
                    download.ImportAttempts);

                var title = string.IsNullOrWhiteSpace(download.Title) ? "Download" : download.Title;
                await toastService.PublishToastAsync(
                    "warning",
                    "Manual Interaction Required",
                    $"{title} could not be imported automatically and has been blocked.",
                    timeoutMs: 8000);

                await historyRepository.AddAsync(new History
                {
                    AudiobookId = download.AudiobookId,
                    AudiobookTitle = download.Title,
                    EventType = "ImportBlocked",
                    Message = message,
                    Source = "AutoImport",
                    Timestamp = DateTime.UtcNow,
                    NotificationSent = false,
                    Data = JsonSerializer.Serialize(new
                    {
                        DownloadId = download.Id,
                        Reason = reason,
                        Attempts = download.ImportAttempts
                    })
                });
            }
        }

        private static string? SelectPrimaryImportedPath(IEnumerable<ImportResult>? results)
        {
            if (results == null)
            {
                return null;
            }

            return results
                .Where(r => r != null && r.Success && !string.IsNullOrWhiteSpace(r.FinalPath))
                .OrderByDescending(r => FileUtils.IsAudioFile(r.FinalPath!) ? 1 : 0)
                .Select(r => r.FinalPath)
                .FirstOrDefault();
        }

        /// <summary>
        /// No clue what this is doing
        /// </summary>
        /// <param name="finalPath"></param>
        /// <param name="blacklist"></param>
        /// <returns></returns>
        private string? ResolveCompletedImportPath(string? finalPath, IEnumerable<string> blacklist)
        {
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                return finalPath;
            }

            if (Directory.Exists(finalPath)
                || FileUtils.IsAudioFile(finalPath)
                || archiveExtractor.IsArchive(finalPath))
            {
                return finalPath;
            }

            if (!File.Exists(finalPath))
            {
                return finalPath;
            }

            var parentDirectory = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrWhiteSpace(parentDirectory) || !Directory.Exists(parentDirectory))
            {
                logger.LogWarning(
                    "ProcessCompletedDownloadAsync: resolved non-audio file path {FinalPath} without an importable parent directory",
                    finalPath);
                return null;
            }

            string[] siblingFiles;
            try
            {
                siblingFiles = System.IO.Directory.GetFiles(parentDirectory, "*", System.IO.SearchOption.AllDirectories)
                    .Where(path => !FileUtils.IsBlacklistedFile(path, blacklist))
                    .ToArray();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "ProcessCompletedDownloadAsync: failed to inspect parent directory for non-audio import path {FinalPath}", finalPath);
                return null;
            }

            var siblingAudioCount = siblingFiles.Count(FileUtils.IsAudioFile);
            if (siblingAudioCount == 0)
            {
                logger.LogWarning(
                    "ProcessCompletedDownloadAsync: resolved non-audio file path {FinalPath} and found no sibling audio files under {ParentDirectory}",
                    finalPath,
                    parentDirectory);
                return null;
            }

            logger.LogInformation(
                "ProcessCompletedDownloadAsync: resolved non-audio file path {FinalPath}; importing parent directory {ParentDirectory} because it contains {AudioCount} audio file(s)",
                finalPath,
                parentDirectory,
                siblingAudioCount);

            return parentDirectory;
        }
    }
}

