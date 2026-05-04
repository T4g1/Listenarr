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

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Default implementation of IFileProcessingHandler extracted from the previous
    /// DownloadProcessingBackgroundService.ProcessMoveOrCopyJobAsync. The implementation
    /// is intentionally conservative: it resolves required scoped services from the provided
    /// IServiceScope and keeps the logic focused and testable.
    /// 
    /// This is a refactor scaffold — some details are simplified to keep the code compact.
    /// Unit tests should be added for each logical branch.
    /// </summary>
    public class DownloadProcessingJobHandler(
        ILogger<DownloadProcessingJobHandler> logger,
        IAppMetricsService metrics,
        IDownloadService downloadService,
        IConfigurationService configurationService,
        IRemotePathMappingService remotePathMappingService,
        IFileMover fileMover) : IFileProcessingHandler
    {

        public async Task HandleAsync(DownloadProcessingJob job, IServiceScope scope, CancellationToken cancellationToken)
        {
            job.AddLogEntry($"Starting file processing: {job.SourcePath}");

            if (string.IsNullOrEmpty(job.SourcePath) || !File.Exists(job.SourcePath))
            {
                job.MarkAsFailed("Download processing job with no source provided");
                return;
            }

            if (string.IsNullOrEmpty(job.DestinationPath))
            {
                job.MarkAsFailed("Download processing job has no destination provided");
                return;
            }

            var localSource = job.SourcePath;

            // Apply path mapping if needed
            if (string.IsNullOrEmpty(job.DownloadClientId))
            {
                job.AddLogEntry($"Download job not assigned to any download client, assuming no remote path mapping is needed");
            }
            else
            {
                await remotePathMappingService.TranslatePathAsync(job.DownloadClientId, job.SourcePath);
                if (!string.Equals(localSource, job.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    job.AddLogEntry($"Applied path mapping: {job.SourcePath} -> {localSource}");
                }
            }

            if (!File.Exists(localSource))
            {
                metrics?.Increment("processing.source_missing");
                job.ScheduleRetry($"Source file not found at processing time: {localSource}");
                return;
            }

            var settings = await configurationService.GetApplicationSettingsAsync();
            job.AddLogEntry($"Retrieved settings - OutputPath: {settings.OutputPath}, EnableMetadataProcessing: {settings.EnableMetadataProcessing}");

            // Ensure unique destination and perform move/copy
            try
            {
                var uniqueDest = FileUtils.GetUniqueDestinationPath(job.DestinationPath);
                var action = settings.CompletedFileAction;

                var success = await fileMover.PerformActionOn(settings.CompletedFileAction, localSource, uniqueDest);
                if (!success)
                {
                    job.MarkAsFailed($"Unable to perform {action} on {localSource} to {uniqueDest}");
                    return;
                }

                job.DestinationPath = uniqueDest;

                // Post-process: update download record and enqueue scan if needed
                await downloadService.ProcessCompletedDownloadAsync(job.DownloadId, job.DestinationPath);
                job.AddLogEntry($"Updated download record with final path: {job.DestinationPath}");
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                job.MarkAsFailed($"File operation failed: {exception.Message}");
                logger.LogError(exception, "File operation failed for job {JobId}", job.Id);
                throw;
            }
        }
    }
}

