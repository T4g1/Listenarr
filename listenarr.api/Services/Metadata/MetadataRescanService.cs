using AsyncKeyedLock;
using Listenarr.Domain.Utils;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Services.Metadata
{
    // Background hosted service to rescan files missing metadata and populate DB fields
    public class MetadataRescanService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MetadataRescanService> _logger;
        private readonly TimeSpan _interval = TimeSpan.FromMinutes(5);
        private readonly AsyncNonKeyedLocker _sem = new(2); // bound concurrent extractions

        public MetadataRescanService(IServiceScopeFactory scopeFactory, ILogger<MetadataRescanService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("MetadataRescanService starting");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                    var candidates = await db.AudiobookFiles
                        .Where(f => f.DurationSeconds == null || f.Format == null || f.SampleRate == null)
                        // Ensure deterministic ordering when using Take() to avoid EF Core warnings
                        .OrderBy(f => f.Id)
                        .Take(20)
                        .ToListAsync(stoppingToken);

                    if (candidates.Any())
                    {
                        _logger.LogInformation("Found {Count} files missing metadata to rescan", candidates.Count);
                    }

                    var tasks = new List<Task>();
                    foreach (var candidate in candidates.Select(f => new { f.Id, f.Path }))
                    {
                        // Start work without passing the stopping token into Task.Run to avoid
                        // TaskCanceledException bubbling up from the runtime; handle cancellation inside.
                        tasks.Add(Task.Run(async () =>
                        {
                            using var releaser = await _sem.LockAsync(stoppingToken);
                            try
                            {
                                using var taskScope = _scopeFactory.CreateScope();
                                var taskDb = taskScope.ServiceProvider.GetRequiredService<ListenArrDbContext>();
                                var taskMetadataService = taskScope.ServiceProvider.GetRequiredService<IMetadataService>();

                                var file = await taskDb.AudiobookFiles.FirstOrDefaultAsync(f => f.Id == candidate.Id, stoppingToken);
                                if (file == null)
                                {
                                    _logger.LogDebug("Skipping metadata rescan for missing file id={Id}", candidate.Id);
                                    return;
                                }

                                if (!FileUtils.IsAudioFile(file.Path ?? string.Empty))
                                {
                                    var audiobook = await taskDb.Audiobooks.FirstOrDefaultAsync(a => a.Id == file.AudiobookId, stoppingToken);
                                    if (audiobook != null && string.Equals(audiobook.FilePath, file.Path, StringComparison.OrdinalIgnoreCase))
                                    {
                                        audiobook.FilePath = null;
                                        audiobook.FileSize = null;
                                    }

                                    taskDb.AudiobookFiles.Remove(file);
                                    await taskDb.SaveChangesAsync(stoppingToken);
                                    _logger.LogInformation("Removed non-audio AudiobookFile entry id={Id} path={Path}", file.Id, LogRedaction.SanitizeFilePath(file.Path));
                                    return;
                                }

                                _logger.LogInformation("Re-extracting metadata for file id={Id} path={Path}", file.Id, LogRedaction.SanitizeFilePath(file.Path));

                                // Bail early if cancellation requested
                                if (stoppingToken.IsCancellationRequested)
                                {
                                    _logger.LogDebug("Cancellation requested before extracting metadata for file id={Id}", file.Id);
                                    return;
                                }

                                var meta = await taskMetadataService.ExtractFileMetadataAsync(file.Path ?? string.Empty);
                                if (meta != null)
                                {
                                    var fi = new System.IO.FileInfo(file.Path ?? string.Empty);
                                    file.Size = fi.Exists ? fi.Length : file.Size;
                                    file.DurationSeconds = meta.Duration.TotalSeconds != 0 ? meta.Duration.TotalSeconds : file.DurationSeconds;
                                    file.Format = !string.IsNullOrEmpty(meta.Format) ? meta.Format : file.Format;
                                    file.Bitrate = meta.Bitrate != 0 ? meta.Bitrate : file.Bitrate;
                                    file.SampleRate = meta.SampleRate != 0 ? meta.SampleRate : file.SampleRate;
                                    file.Channels = meta.Channels != 0 ? meta.Channels : file.Channels;

                                    // Save changes; respect cancellation
                                    await taskDb.SaveChangesAsync(stoppingToken);
                                    _logger.LogInformation("Updated metadata for file id={Id}", file.Id);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                // Cancellation requested - ignore and let the service shutdown gracefully
                                _logger.LogDebug("Metadata rescan cancelled for file id={Id}", candidate.Id);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                                _logger.LogWarning(ex, "Failed to rescan metadata for file id={Id} path={Path}", candidate.Id, LogRedaction.SanitizeFilePath(candidate.Path));
                            }
                        }));
                    }

                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Metadata rescan cycle canceled/timed out; continuing");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogError(ex, "Error while running metadata rescan");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            _logger.LogInformation("MetadataRescanService stopping");
        }
    }
}


