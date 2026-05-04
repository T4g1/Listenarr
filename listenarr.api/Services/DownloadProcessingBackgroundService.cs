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
using Listenarr.Domain.Common;
using Listenarr.Application.Models.Configurations;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Models.Enumerations;
using Listenarr.Domain.Models.Exceptions;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Background service that processes the download post-processing queue
    /// </summary>
    public class DownloadProcessingBackgroundService(
        ILogger<DownloadProcessingBackgroundService> logger,
        IAppMetricsService metrics,
        IDownloadProcessingQueueService downloadProcessingQueueService,
        IFileMover fileMover,
        IAudiobookRepository audiobookRepository,
        IDownloadRepository downloadRepository,
        IDownloadService downloadService,
        IMetadataService metadataService,
        IFileNamingService fileNamingService,
        IRemotePathMappingService remotePathMappingService,
        IConfigurationService configurationService,
        IDownloadProcessingJobRepository downloadProcessingJobRepository,
        IDownloadItemService downloadItemService,
        IScanQueueService scanQueueService) : BackgroundService
    {
        private readonly TimeSpan _processingInterval = TimeSpan.FromSeconds(10); // Check every 10 seconds

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Download Processing Background Service started");

            // On startup, reset any jobs stuck in Processing status (from previous crash/restart)
            try
            {
                await ResetStuckJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Download processing startup reset canceled during shutdown");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "Download processing startup reset canceled/timed out; continuing");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Failed to reset stuck jobs on startup");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Ensure any previously completed downloads are enqueued for processing
                    await EnqueueCompletedDownloadsAsync(stoppingToken);

                    await ProcessQueueAsync(stoppingToken);
                    await ProcessRetryJobsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    logger.LogWarning(ex, "Download processing cycle canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogError(ex, "Error processing download queue");
                }

                try
                {
                    await Task.Delay(_processingInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            logger.LogInformation("Download Processing Background Service stopped");
        }

        // Use FileUtils.GetUniqueDestinationPath instead of a local implementation

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            var job = await downloadProcessingQueueService.GetNextJobAsync();
            if (job == null) return;

            logger.LogInformation("Processing job {JobId} for download {DownloadId}: {JobType}",
                job.Id, job.DownloadId, job.JobType);

            // Mark job as processing
            job.Status = ProcessingJobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            job.AddLogEntry("Started processing");
            await downloadProcessingQueueService.UpdateJobAsync(job);

            try
            {
                await ProcessJobAsync(job, cancellationToken);

                // Only mark the job as completed if it is still in Processing state.
                // Some job handlers may set the job to Failed/Retry/Skipped and we should respect that.
                if (job.Status == ProcessingJobStatus.Processing)
                {
                    job.MarkAsCompleted();
                    logger.LogInformation("Successfully completed job {JobId} for download {DownloadId}",
                        job.Id, job.DownloadId);
                }
                else
                {
                    logger.LogInformation("Job {JobId} for download {DownloadId} finished with status {Status}", job.Id, job.DownloadId, job.Status);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Failed to process job {JobId} for download {DownloadId}: {Error}",
                    job.Id, job.DownloadId, ex.Message);

                job.AddLogEntry($"Processing failed: {ex.Message}");
                job.ScheduleRetry();
            }

            await downloadProcessingQueueService.UpdateJobAsync(job);
        }

        /// <summary>
        /// Reset jobs that were stuck in Processing status from a previous session (e.g., after crash or restart).
        /// This prevents orphaned jobs from blocking new finalization attempts.
        /// </summary>
        private async Task ResetStuckJobsAsync(CancellationToken cancellationToken)
        {
            var stuckJobs = await downloadProcessingJobRepository.GetStuckProcessingJobsAsync();

            if (stuckJobs.Any())
            {
                logger.LogInformation("Found {Count} stuck jobs in Processing status, resetting to Pending", stuckJobs.Count);
                foreach (var job in stuckJobs)
                {
                    job.Status = ProcessingJobStatus.Pending;
                    job.AddLogEntry("Reset from stuck Processing state after service restart");
                    logger.LogInformation("Reset stuck job {JobId} for download {DownloadId}", job.Id, job.DownloadId);
                    await downloadProcessingJobRepository.UpdateAsync(job);
                }
            }
        }

        private async Task ProcessRetryJobsAsync(CancellationToken cancellationToken)
        {
            var retryJobs = await downloadProcessingQueueService.GetRetryJobsAsync();

            foreach (var job in retryJobs)
            {
                logger.LogInformation("Retrying job {JobId} for download {DownloadId} (attempt {Attempt}/{MaxAttempts})",
                    job.Id, job.DownloadId, job.RetryCount + 1, job.MaxRetries);

                // Reset job to pending for processing
                job.Status = ProcessingJobStatus.Pending;
                job.ErrorMessage = null;
                job.AddLogEntry($"Retry #{job.RetryCount} scheduled");

                await downloadProcessingQueueService.UpdateJobAsync(job);
            }
        }

        /// <summary>
        /// Find completed downloads that are not yet enqueued for processing and add them to the queue.
        /// This runs briefly each loop to ensure existing completed items are eventually processed.
        /// </summary>
        private async Task EnqueueCompletedDownloadsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Build a set of enabled download client IDs so we skip downloads from disabled clients
                HashSet<string> enabledClientIds;
                try
                {
                    var allClients = await configurationService.GetDownloadClientConfigurationsAsync();
                    enabledClientIds = new HashSet<string>(
                        allClients.Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id)).Select(c => c.Id!),
                        StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                {
                    logger.LogDebug(exception, "Failed to load download client configurations for enabled-client filtering");
                    enabledClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                // Find recent completed downloads that have not yet been processed into jobs.
                // Include Processing status to recover downloads orphaned by a crash/restart
                // that occurred after FinalizeDownloadAsync set the status but before the
                // processing job was queued.
                var candidates = await downloadRepository.GetCompletionCandidatesAsync(200);

                // Filter out downloads from disabled or missing clients
                var originalCount = candidates.Count;
                candidates = candidates.Where(d =>
                    string.IsNullOrWhiteSpace(d.DownloadClientId) ||
                    string.Equals(d.DownloadClientId, "DDL", StringComparison.OrdinalIgnoreCase) ||
                    enabledClientIds.Contains(d.DownloadClientId)).ToList();
                if (candidates.Count < originalCount)
                {
                    logger.LogDebug("Skipping {Count} completed downloads from disabled/missing download clients",
                        originalCount - candidates.Count);
                }

                // Get download IDs that already have active jobs to avoid N+1 queries
                var candidateIds = candidates.Select(d => d.Id).ToList();
                var alreadyQueuedIds = new HashSet<string>(
                    await downloadProcessingJobRepository.GetPendingDownloadIdsAsync(candidateIds),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var dl in candidates)
                {
                    if (dl.DownloadClientId == null)
                    {
                        throw new InvalidOperationException($"Download {dl.Id} had no download client linked to it");
                    }

                    if (string.IsNullOrEmpty(dl.DownloadPath))
                    {
                        throw new InvalidOperationException($"Download {dl.Id} path is not set, processing aborted");
                    }

                    // Skip if there is already a job for this download pending/processing/retry
                    if (alreadyQueuedIds.Contains(dl.Id))
                    {
                        continue;
                    }

                    // Queue for processing using the resolved path
                    await downloadProcessingQueueService.QueueDownloadProcessingAsync(dl.Id, dl.DownloadPath, dl.DownloadClientId);
                    logger.LogInformation("Enqueued completed download {DownloadId} for processing: {Source}", dl.Id, dl.DownloadPath);
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                logger.LogWarning(exception, "Error while enqueuing completed downloads");
            }
        }

        private async Task ProcessJobAsync(DownloadProcessingJob job, CancellationToken cancellationToken)
        {
            switch (job.JobType)
            {
                case ProcessingJobType.MoveOrCopyFile:
                    await ProcessMoveOrCopyJobAsync(job, cancellationToken);
                    break;
                case ProcessingJobType.ExtractMetadata:
                    // Older jobs in the queue may use job types that are no longer supported.
                    // Mark them as failed with a helpful message and do not throw to avoid retry storms.
                    job.AddLogEntry("Job type ExtractMetadata is not supported");
                    job.ErrorMessage = "Job type ExtractMetadata is not supported";
                    job.Status = ProcessingJobStatus.Failed;
                    job.CompletedAt = DateTime.UtcNow;
                    break;
                default:
                    throw new NotSupportedException($"Job type {job.JobType} is not supported");
            }
        }

        private async Task ProcessMoveOrCopyJobAsync(DownloadProcessingJob job, CancellationToken cancellationToken)
        {
            job.AddLogEntry($"Starting file processing: {job.SourcePath}");

            if (job.DownloadClientId == null)
            {
                throw new InvalidOperationException($"Job trying to process download {job.DownloadId} unrelated to any download client");
            }

            if (job.SourcePath == null)
            {
                throw new InvalidOperationException($"Job for download {job.DownloadId} has no source path");
            }

            if (string.IsNullOrEmpty(job.SourcePath) || (!File.Exists(job.SourcePath) && !Directory.Exists(job.SourcePath)))
            {
                var localPath = await remotePathMappingService.TranslatePathAsync(job.DownloadClientId, job.SourcePath);
                if (!string.Equals(localPath, job.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    job.AddLogEntry($"Applied path mapping: {job.SourcePath} -> {localPath}");
                    job.SourcePath = localPath;
                }

                if (!File.Exists(localPath) && !Directory.Exists(localPath))
                {
                    // Source missing at processing-time. Schedule a retry instead of throwing so transient
                    // races (file still being moved by another process) don't permanently fail the job.
                    job.AddLogEntry($"Source path not found at processing time: {localPath}");
                    metrics.Increment("processing.source_missing");
                    job.ScheduleRetry();
                    job.ErrorMessage = $"Source path not found at processing time: {localPath}";
                    return;
                }
            }

            // Get application settings
            var settings = await configurationService.GetApplicationSettingsAsync();
            job.AddLogEntry($"Retrieved settings - OutputPath: {settings.OutputPath}, EnableMetadataProcessing: {settings.EnableMetadataProcessing}");

            // If the source is a directory (multi-file download), enumerate all importable files and process each one
            if (Directory.Exists(job.SourcePath) && !File.Exists(job.SourcePath))
            {
                job.AddLogEntry($"Source is a directory, scanning for importable files: {job.SourcePath}");
                var importableFiles = new List<string>();

                var download = await downloadRepository.GetByIdAsync(job.DownloadId);
                if (download != null)
                {
                    try
                    {
                        importableFiles = await downloadItemService.MatchLocalAndDownloadedFilesAsync(download, job.SourcePath, cancellationToken);
                    }
                    catch (DownloadProcessingException exception)
                    {
                        // FIXME: Should we really still process unfiltered files in that case ?
                        logger.LogWarning(exception, exception.Message);

                        importableFiles = [.. Directory.EnumerateFiles(job.SourcePath, "*.*", SearchOption.AllDirectories)
                                .Where(f => !FileUtils.IsBlacklistedFile(f, settings.ImportBlacklistExtensions))
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
                    }
                }

                if (importableFiles.Count == 0)
                {
                    job.AddLogEntry("No importable files found in directory (all files blacklisted or skipped)");
                    job.ErrorMessage = "No importable files found in source directory";
                    job.Status = ProcessingJobStatus.Failed;
                    job.CompletedAt = DateTime.UtcNow;
                    return;
                }

                job.AddLogEntry($"Found {importableFiles.Count} importable file(s) to process");
                var originalSourcePath = job.SourcePath;
                string? primaryCompletionPath = null;
                string? fallbackCompletionPath = null;
                foreach (var file in importableFiles)
                {
                    job.SourcePath = file;
                    job.AddLogEntry($"Processing file: {file}");
                    await ProcessFileWithEnhancedLogicAsync(
                        job,
                        settings,
                        cancellationToken,
                        finalizeDownload: false);

                    if (!string.IsNullOrWhiteSpace(job.DestinationPath))
                    {
                        fallbackCompletionPath ??= job.DestinationPath;
                        if (FileUtils.IsAudioFile(file))
                        {
                            primaryCompletionPath ??= job.DestinationPath;
                        }
                    }

                    if (job.Status == ProcessingJobStatus.Failed || job.Status == ProcessingJobStatus.Retry)
                    {
                        job.AddLogEntry($"Failed processing file: {file}, stopping directory processing");
                        break;
                    }
                }
                job.SourcePath = originalSourcePath;

                if (job.Status != ProcessingJobStatus.Failed && job.Status != ProcessingJobStatus.Retry)
                {
                    var completionPath = primaryCompletionPath ?? fallbackCompletionPath;

                    if (importableFiles.Count > 1)
                    {
                        // We give a directory in which to import the files
                        completionPath = Path.GetDirectoryName(completionPath);

                        // The directory must exist
                        if (!string.IsNullOrWhiteSpace(completionPath))
                        {
                            Directory.CreateDirectory(completionPath);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(completionPath))
                    {
                        job.DestinationPath = completionPath;
                        job.AddLogEntry($"Finalizing directory batch with destination: {job.DestinationPath} from {job.SourcePath}");
                        await FinalizeProcessedDownloadAsync(job, downloadService);
                    }
                }

                return;
            }

            // Process the file using the enhanced logic from ProcessCompletedDownloadAsync
            await ProcessFileWithEnhancedLogicAsync(job, settings, cancellationToken);
        }

        private async Task ProcessFileWithEnhancedLogicAsync(
            DownloadProcessingJob job,
            ApplicationSettings settings,
            CancellationToken cancellationToken,
            bool finalizeDownload = true)
        {
            var sourcePath = job.SourcePath!;
            string destinationPath;

            // Handle file move/copy operations if configured
            if (!string.IsNullOrEmpty(settings.OutputPath))
            {
                job.AddLogEntry($"Processing with output path: {settings.OutputPath}");

                // Determine destination path based on settings
                if (settings.EnableMetadataProcessing)
                {
                    job.AddLogEntry("Using file naming service for destination path");

                    var dl = await downloadRepository.FindAsync(job.DownloadId);
                    if (dl == null)
                    {
                        throw new InvalidOperationException($"Cannot process download {job.DownloadId}: Not found");
                    }

                    if (dl.AudiobookId == null)
                    {
                        throw new InvalidOperationException($"Cannot process download {job.DownloadId}: Does not have an audiobook linked to it");
                    }

                    var audiobook = await audiobookRepository.GetByIdAsync((int)dl.AudiobookId);
                    if (audiobook == null)
                    {
                        throw new InvalidOperationException($"Cannot process download {job.DownloadId}: Cannot retrieve audiobook {dl.AudiobookId}");
                    }

                    var metadata = await metadataService.FetchMetadataAsync(job, dl, audiobook, cancellationToken);

                    // For processing jobs, compute the appropriate destination directory first.
                    // If the download is linked to an audiobook and the audiobook has a BasePath,
                    // prefer that as the base directory (and use filename-only pattern in those
                    // cases). Otherwise use the configured OutputPath. We will place the file into
                    // the destination directory using the original filename first, then later
                    // ProcessCompletedDownloadAsync will apply the full naming pattern (including
                    // creating subfolders when allowed).
                    var ext = Path.GetExtension(sourcePath);
                    var basePathForFile = settings.OutputPath;

                    // If the download links to an audiobook and we've built an audiobook naming
                    // metadata above, prefer the audiobook BasePath and switch to a filename-only
                    // pattern so we don't create arbitrary folders inside an audiobook base path.
                    if (audiobook != null && !string.IsNullOrWhiteSpace(audiobook.BasePath))
                    {
                        basePathForFile = audiobook.BasePath;

                        // If a global pattern exists, use only the filename portion when an
                        // audiobook BasePath is present; this avoids creating unintended
                        // subfolders under the audiobook base path.
                        // Use the configured filename pattern in full when computing the
                        // tentative generated path relative to the audiobook BasePath.
                    }

                    // Now generate a tentative path using the filename-only or relative pattern
                    // so we can compute the destination directory. We'll not actually apply the
                    // full pattern on the source; instead we will place the file into destDir
                    // using original filename first.
                    var generatedPath = await fileNamingService.GenerateFilePathAsync(metadata, basePathForFile, ext);

                    // Preserve subdirectories from the generated path. The naming pattern may include
                    // subfolders (e.g. {Author}/{Series}/...). If the generatedPath is rooted, use it
                    // directly. If it's relative, combine it with the configured OutputPath so subfolders
                    // are retained instead of being stripped to a single filename.
                    logger.LogDebug("GeneratedPath from FileNamingService: {GeneratedPath} (rooted={IsRooted})", generatedPath, Path.IsPathRooted(generatedPath));

                    // Only allow subfolders if the naming pattern includes DiskNumber or ChapterNumber
                    var fullPattern = settings.FileNamingPattern ?? string.Empty;
                    var patternAllowsSubfolders = fullPattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                        || fullPattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0;

                    // Compute the destinationPath so we know where to place the file initially.
                    if (Path.IsPathRooted(generatedPath))
                    {
                        destinationPath = generatedPath;
                    }
                    else
                    {
                        var outputRoot = basePathForFile ?? string.Empty;

                        if (!patternAllowsSubfolders)
                        {
                            // Force filename-only: take only the filename portion of generatedPath and sanitize it
                            var forcedFilename = Path.GetFileName(generatedPath) ?? Path.GetFileName(sourcePath);
                            try
                            {
                                var invalid = Path.GetInvalidFileNameChars();
                                var sb = new System.Text.StringBuilder();
                                foreach (var c in forcedFilename)
                                {
                                    sb.Append(invalid.Contains(c) ? '_' : c);
                                }
                                forcedFilename = sb.ToString();
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                job.AddLogEntry($"Failed to sanitize forced filename: {ex.Message}");
                            }

                            var relativeForcedFilename = forcedFilename.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            if (string.IsNullOrWhiteSpace(outputRoot))
                            {
                                destinationPath = relativeForcedFilename;
                            }
                            else
                            {
                                var normalizedOutputRoot = outputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                destinationPath = normalizedOutputRoot + Path.DirectorySeparatorChar + relativeForcedFilename;
                            }
                            job.AddLogEntry($"Pattern does not allow subfolders. Forced filename-only destination: {destinationPath}");
                        }
                        else
                        {
                            var relativeGeneratedPath = generatedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            if (string.IsNullOrWhiteSpace(outputRoot))
                            {
                                destinationPath = relativeGeneratedPath;
                            }
                            else
                            {
                                var normalizedOutputRoot = outputRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                destinationPath = normalizedOutputRoot + Path.DirectorySeparatorChar + relativeGeneratedPath;
                            }
                        }
                    }

                    job.AddLogEntry($"Initial destination inside output root: {destinationPath}");
                    try
                    {
                        var destDirForCheck = Path.GetDirectoryName(destinationPath) ?? string.Empty;
                        var exists = !string.IsNullOrEmpty(destDirForCheck) && Directory.Exists(destDirForCheck);
                        var root = string.Empty;
                        try { root = Path.GetPathRoot(destDirForCheck) ?? string.Empty; } catch (Exception caughtEx_4) when (caughtEx_4 is not OperationCanceledException && caughtEx_4 is not OutOfMemoryException && caughtEx_4 is not StackOverflowException) { root = string.Empty; }
                        job.AddLogEntry($"Destination dir exists: {exists} PathRoot={root}");

                        if (!string.IsNullOrEmpty(root) && string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), destDirForCheck.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        {
                            job.AddLogEntry($"Warning: destination dir is a root path: {destDirForCheck}");
                        }
                    }
                    catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                    {
                        job.AddLogEntry($"Failed to inspect destination directory: {exception.Message}");
                    }
                }
                else
                {
                    // Simple naming - use original filename in output directory
                    var fileName = Path.GetFileName(sourcePath);
                    destinationPath = Path.Join(settings.OutputPath, fileName);
                    job.AddLogEntry($"Using simple destination: {destinationPath}");
                }

                // Determine destination directory but DO NOT create it during import/processing
                var destDir = Path.GetDirectoryName(destinationPath);

                // Check if already moved (use download loaded from dbContext above)
                var download = await downloadRepository.FindAsync(job.DownloadId);
                if (download != null && download.Status == DownloadStatus.Moved)
                {
                    job.AddLogEntry("File already moved by DownloadService. Skipping background move.");
                    job.DestinationPath = destinationPath;
                    return;
                }

                if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                {
                    // Sonarr parity: ImportMode.Auto - if CanMoveFiles is true, Move; otherwise Copy.
                    // This prevents moving files from active seeders (which breaks the torrent).
                    // Falls back to configured CompletedFileAction if CanMoveFiles metadata is not present.
                    var action = settings.CompletedFileAction;

                    if (download?.Metadata != null && download.Metadata.TryGetValue("CanMoveFiles", out var canMoveObj))
                    {
                        bool canMoveFiles = canMoveObj is bool b ? b : (canMoveObj is System.Text.Json.JsonElement je ? je.GetBoolean() : bool.TryParse(canMoveObj?.ToString(), out var parsed) && parsed);
                        if (!canMoveFiles && action == FileAction.Move)
                        {
                            action = FileAction.Copy;
                            job.AddLogEntry("Torrent is still seeding (CanMoveFiles=false). Using Copy instead of Move to preserve seeder.");
                            logger.LogInformation("Download {DownloadId}: CanMoveFiles=false, downgrading Move to Copy to preserve active seeder", job.DownloadId);
                        }
                    }

                    job.AddLogEntry($"Performing {action} operation");

                    // Capture source size before operation for later verification (move will remove source)
                    long sourceSize = new FileInfo(sourcePath).Length;

                    try
                    {
                        // Ensure unique destination to avoid overwriting
                        logger.LogDebug("Resolving unique destination for background job: {Dest}", destinationPath);
                        destinationPath = FileUtils.GetUniqueDestinationPath(destinationPath);
                        var success = await fileMover.PerformActionOn(action, sourcePath, destinationPath);

                        // Verification: ensure destination exists and (if sourceSize available) sizes match
                        if (!File.Exists(destinationPath))
                        {
                            job.AddLogEntry($"Destination not found after {action}: {destinationPath}");
                            job.ErrorMessage = $"Destination not found after {action}";
                            throw new IOException($"Destination not found after {action}: {destinationPath}");
                        }

                        try
                        {
                            var destSize = new FileInfo(destinationPath).Length;
                            if (destSize != sourceSize)
                            {
                                job.MarkAsFailed($"Destination size ({destSize}) does not match source size ({sourceSize})");
                                throw new IOException("Destination size mismatch after file operation");
                            }
                        }
                        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                        {
                            // If verifying size fails for any reason, record and surface the error
                            job.MarkAsFailed($"Failed to verify destination size: {exception.Message}");
                            throw;
                        }

                        job.AddLogEntry($"Verified destination: {destinationPath} (size: {new FileInfo(destinationPath).Length})");
                        job.DestinationPath = destinationPath;
                    }
                    catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
                    {
                        job.MarkAsFailed($"File operation failed: {exception.Message}");
                        throw;
                    }
                }
                else
                {
                    job.AddLogEntry("Source and destination are the same, no file operation needed");
                    job.DestinationPath = sourcePath;
                }
            }
            else
            {
                job.AddLogEntry("No output path configured, keeping file at original location");
                job.DestinationPath = sourcePath;
            }

            if (finalizeDownload && !string.IsNullOrWhiteSpace(job.DestinationPath))
            {
                await FinalizeProcessedDownloadAsync(job, downloadService);
            }
        }

        private async Task FinalizeProcessedDownloadAsync(DownloadProcessingJob job, IDownloadService downloadService)
        {
            if (job.SourcePath == null)
            {
                throw new ArgumentNullException(nameof(job), "Job.SourcePath is required");
            }

            await downloadService.ProcessCompletedDownloadAsync(job.DownloadId, job.SourcePath);
            job.AddLogEntry($"Updated download record with source path: {job.SourcePath}");

            try
            {
                var dl = await downloadRepository.FindAsync(job.DownloadId);
                if (dl == null || dl.AudiobookId == null)
                {
                    return;
                }

                var audiobook = await audiobookRepository.GetByIdAsync(dl.AudiobookId.Value);
                if (audiobook == null)
                {
                    return;
                }

                // Enqueue a scan using the audiobook's configured library path (null)
                // rather than the download/destination path. The import process already
                // hardlinks/copies files into the library folder, so the scanner should
                // verify the library location and not the download directory, which would
                // trigger spurious "Refusing to associate file outside audiobook folder"
                // warnings from AudioFileService.
                var jobId = await scanQueueService.EnqueueScanAsync(audiobook, null);
                job.AddLogEntry($"Enqueued scan job {jobId} for audiobook {dl.AudiobookId}");
                logger.LogInformation("Enqueued scan job {JobId} for audiobook {AudiobookId} after processing download {DownloadId}", jobId, dl.AudiobookId, job.DownloadId);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                job.AddLogEntry($"Failed to enqueue scan job: {exception.Message}");
            }
        }
    }
}


