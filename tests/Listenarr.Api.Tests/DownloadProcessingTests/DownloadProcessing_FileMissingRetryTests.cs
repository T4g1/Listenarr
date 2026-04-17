using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Metadata;

namespace Listenarr.Api.Tests
{
    [Trait("Category", "DownloadProcessing")]
    public class DownloadProcessing_FileMissingRetryTests
    {
        [Fact]
        public async Task ProcessMoveOrCopy_IfSourceMissing_SchedulesRetryAndRecordsMetric()
        {
            var dbOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(dbOptions);

            // Create temp source file then delete it to simulate race
            var sourceFile = Path.Join(Path.GetTempPath(), $"dl-missing-{Guid.NewGuid()}.mp3");
            await File.WriteAllTextAsync(sourceFile, "test");

            // Destination directory must exist for background service to attempt operations
            var destRoot = Path.Join(Path.GetTempPath(), $"dl-dest-{Guid.NewGuid()}");
            Directory.CreateDirectory(destRoot);

            // Add a download record and a processing job
            var dl = new Download
            {
                Id = "missing-test-1",
                Status = DownloadStatus.Completed,
                DownloadPath = sourceFile,
                FinalPath = sourceFile,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            db.Downloads.Add(dl);
            await db.SaveChangesAsync();

            // Setup DI + services
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(db);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings
            {
                OutputPath = destRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false
            });
            services.AddSingleton<IConfigurationService>(configMock.Object);

            var downloadServiceMock = new Mock<IDownloadService>();
            services.AddSingleton<IDownloadService>(downloadServiceMock.Object);

            // Queue service uses DbContext and logger - register real instance
            services.AddScoped<IDownloadProcessingQueueService, DownloadProcessingQueueService>();

            // Mock IImportItemResolutionService - just returns the preliminary item unchanged
            var importResolutionMock = new Mock<IImportItemResolutionService>();
            importResolutionMock
                .Setup(x => x.ResolveImportItemAsync(It.IsAny<Download>(), It.IsAny<QueueItem>(), It.IsAny<QueueItem>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download d, QueueItem q, QueueItem? p, CancellationToken ct) => q);
            services.AddScoped<IImportItemResolutionService>(_ => importResolutionMock.Object);

            var metricsMock = new Mock<IAppMetricsService>();
            services.AddSingleton<IAppMetricsService>(metricsMock.Object);
            services.AddSingleton(new Mock<IFfmpegService>().Object);
            services.AddSingleton(new Mock<HttpClient>().Object);
            services.AddSingleton<IProcessRunner, SystemProcessRunner>();
            services.AddScoped<IMetadataService, MetadataService>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadProcessingBackgroundService>>();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var queueService = provider.GetRequiredService<IDownloadProcessingQueueService>();

            // Enqueue the job pointing to the source file
            var jobId = await queueService.QueueDownloadProcessingAsync(dl.Id, sourceFile, null);
            var job = await queueService.GetJobAsync(jobId);

            // Delete the source to simulate disappearance before processing
            try { File.Delete(sourceFile); } catch (IOException ex) { _ = ex; } catch (UnauthorizedAccessException ex) { _ = ex; }

            // Create the background service instance (no longer needs importItemResolution in constructor)
            var svc = new DownloadProcessingBackgroundService(scopeFactory, loggerMock.Object, metricsMock.Object);

            // Set job to processing (the outer loop normally does this)
            job.Status = ProcessingJobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            await queueService.UpdateJobAsync(job);

            using var scope = provider.CreateScope();

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            // Invoke and await the returned Task
            var task = (Task)method!.Invoke(svc, new object[] { job, scope, CancellationToken.None })!;
            await task;

            // Persist job updates (ProcessQueueAsync usually updates job at the end)
            await queueService.UpdateJobAsync(job);

            // Reload job
            var updated = await queueService.GetJobAsync(job.Id);
            Assert.NotNull(updated);
            Assert.Equal(ProcessingJobStatus.Retry, updated!.Status);
            Assert.True(updated.RetryCount >= 1);
            Assert.Contains("not found", updated.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // Ensure metrics increment was recorded for missing source
            metricsMock.Verify(m => m.Increment("processing.source_missing", It.IsAny<double>()), Times.AtLeastOnce);

            // Cleanup created temp destination dir
            try { Directory.Delete(destRoot, true); }
            catch (IOException) { /* Best-effort cleanup for temp test directories. */ }
            catch (UnauthorizedAccessException) { /* Best-effort cleanup for temp test directories. */ }
        }

        [Fact]
        public async Task ProcessMoveOrCopy_DirectorySource_FinalizesOnceAfterCompanionFilesAreCopied()
        {
            var dbOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(dbOptions);

            var sourceDir = Path.Join(Path.GetTempPath(), $"dl-dir-{Guid.NewGuid()}");
            var destRoot = Path.Join(Path.GetTempPath(), $"dl-dest-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(destRoot);

            var audioPath = Path.Join(sourceDir, "book.m4b");
            var coverPath = Path.Join(sourceDir, "cover.jpg");
            var txtPath = Path.Join(sourceDir, "book.txt");
            await File.WriteAllTextAsync(audioPath, "audio");
            await File.WriteAllTextAsync(coverPath, "cover");
            await File.WriteAllTextAsync(txtPath, "notes");

            var dl = new Download
            {
                Id = "dir-batch-test-1",
                Status = DownloadStatus.Completed,
                DownloadPath = sourceDir,
                FinalPath = sourceDir,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow
            };
            db.Downloads.Add(dl);
            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(db);
            services.AddSingleton(new Mock<IImportItemResolutionService>().Object);
            services.AddSingleton(new Mock<IFfmpegService>().Object);
            services.AddSingleton(new Mock<HttpClient>().Object);
            services.AddSingleton<IProcessRunner, SystemProcessRunner>();
            services.AddMemoryCache(); 
            services.AddScoped<IMetadataService, MetadataService>();
            services.AddScoped<IRemotePathMappingService, RemotePathMappingService>();
            

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings
            {
                OutputPath = destRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false
            });
            services.AddSingleton<IConfigurationService>(configMock.Object);

            string? processedPath = null;
            var downloadServiceMock = new Mock<IDownloadService>();
            downloadServiceMock
                .Setup(d => d.ProcessCompletedDownloadAsync(dl.Id, It.IsAny<string>()))
                .Callback<string, string>((id, path) =>
                {
                    processedPath = path;
                    var tracked = db.Downloads.Find(id);
                    if (tracked != null)
                    {
                        tracked.Status = DownloadStatus.Moved;
                        tracked.FinalPath = path;
                        db.SaveChanges();
                    }
                })
                .Returns(Task.CompletedTask);
            services.AddSingleton<IDownloadService>(downloadServiceMock.Object);

            var metricsMock = new Mock<IAppMetricsService>();
            services.AddSingleton<IAppMetricsService>(metricsMock.Object);

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadProcessingBackgroundService>>();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var svc = new DownloadProcessingBackgroundService(scopeFactory, loggerMock.Object, metricsMock.Object);

            var job = new DownloadProcessingJob
            {
                Id = "job-dir-batch-1",
                DownloadId = dl.Id,
                JobType = ProcessingJobType.MoveOrCopyFile,
                Status = ProcessingJobStatus.Processing,
                SourcePath = sourceDir
            };

            using var scope = provider.CreateScope();

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(svc, new object[] { job, scope, CancellationToken.None })!;
            await task;

            var expectedAudioDest = Path.Join(destRoot, "book.m4b");
            var expectedCoverDest = Path.Join(destRoot, "cover.jpg");
            var expectedTxtDest = Path.Join(destRoot, "book.txt");

            Assert.True(File.Exists(expectedAudioDest));
            Assert.True(File.Exists(expectedCoverDest));
            Assert.True(File.Exists(expectedTxtDest));
            Assert.Equal(sourceDir, processedPath);
            Assert.Equal(destRoot, job.DestinationPath);

            downloadServiceMock.Verify(d => d.ProcessCompletedDownloadAsync(dl.Id, sourceDir), Times.Once);

            try { Directory.Delete(sourceDir, true); }
            catch (IOException) { /* Best-effort cleanup for temp test directories. */ }
            catch (UnauthorizedAccessException) { /* Best-effort cleanup for temp test directories. */ }

            try { Directory.Delete(destRoot, true); }
            catch (IOException) { /* Best-effort cleanup for temp test directories. */ }
            catch (UnauthorizedAccessException) { /* Best-effort cleanup for temp test directories. */ }
        }

        [Fact]
        [Trait("Method", "ProcessMoveOrCopyJobAsync")]
        public async Task ProcessMoveOrCopy_DirectorySource_UsesClientReportedFilesToExcludeUnrelatedFiles()
        {
            var dbOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(dbOptions);

            var sourceDir = Path.Join(Path.GetTempPath(), $"dl-dir-{Guid.NewGuid()}");
            var destRoot = Path.Join(Path.GetTempPath(), $"dl-dest-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(destRoot);

            var audioPath = Path.Join(sourceDir, "book.m4b");
            var coverPath = Path.Join(sourceDir, "cover.jpg");
            var txtPath = Path.Join(sourceDir, "book.txt");
            var unrelatedPath = Path.Join(sourceDir, "unrelated.txt");
            await File.WriteAllTextAsync(audioPath, "audio");
            await File.WriteAllTextAsync(coverPath, "cover");
            await File.WriteAllTextAsync(txtPath, "notes");
            await File.WriteAllTextAsync(unrelatedPath, "ignore");

            var dl = new Download
            {
                Id = "dir-batch-client-scope-1",
                Status = DownloadStatus.Completed,
                DownloadPath = sourceDir,
                FinalPath = sourceDir,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                DownloadClientId = "client-1",
                Metadata = new Dictionary<string, object>
                {
                    ["TorrentHash"] = "ABC123"
                }
            };
            db.Downloads.Add(dl);
            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(db);

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings
            {
                OutputPath = destRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false
            });
            services.AddSingleton<IConfigurationService>(configMock.Object);

            var importResolverMock = new Mock<IImportItemResolutionService>();
            importResolverMock
                .Setup(r => r.ResolveImportItemAsync(
                    It.Is<Download>(d => d.Id == dl.Id),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download download, QueueItem queueItem, QueueItem? previousAttempt, CancellationToken ct) =>
                {
                    queueItem.SourceFiles = new List<string> { audioPath, coverPath, txtPath };
                    return queueItem;
                });
            services.AddSingleton<IImportItemResolutionService>(importResolverMock.Object);

            string? processedPath = null;
            var downloadServiceMock = new Mock<IDownloadService>();
            downloadServiceMock
                .Setup(d => d.ProcessCompletedDownloadAsync(dl.Id, It.IsAny<string>()))
                .Callback<string, string>((id, path) =>
                {
                    processedPath = path;
                    var tracked = db.Downloads.Find(id);
                    if (tracked != null)
                    {
                        tracked.Status = DownloadStatus.Moved;
                        tracked.FinalPath = path;
                        db.SaveChanges();
                    }
                })
                .Returns(Task.CompletedTask);
            services.AddSingleton<IDownloadService>(downloadServiceMock.Object);

            var metricsMock = new Mock<IAppMetricsService>();
            services.AddSingleton<IAppMetricsService>(metricsMock.Object);
            
            services.AddMemoryCache(); 
            services.AddScoped<IRemotePathMappingService, RemotePathMappingService>();
            services.AddSingleton(new Mock<IFfmpegService>().Object);
            services.AddSingleton(new Mock<HttpClient>().Object);
            services.AddSingleton<IProcessRunner, SystemProcessRunner>();
            services.AddScoped<IMetadataService, MetadataService>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadProcessingBackgroundService>>();

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var svc = new DownloadProcessingBackgroundService(scopeFactory, loggerMock.Object, metricsMock.Object);

            var job = new DownloadProcessingJob
            {
                Id = "job-dir-batch-client-scope-1",
                DownloadId = dl.Id,
                JobType = ProcessingJobType.MoveOrCopyFile,
                Status = ProcessingJobStatus.Processing,
                SourcePath = sourceDir
            };

            using var scope = provider.CreateScope();
            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(svc, new object[] { job, scope, CancellationToken.None })!;
            await task;

            Assert.True(File.Exists(Path.Join(destRoot, "book.m4b")));
            Assert.True(File.Exists(Path.Join(destRoot, "cover.jpg")));
            Assert.True(File.Exists(Path.Join(destRoot, "book.txt")));
            Assert.False(File.Exists(Path.Join(destRoot, "unrelated.txt")));
            Assert.Equal(sourceDir, processedPath);

            try { Directory.Delete(sourceDir, true); }
            catch (IOException) { /* Best-effort cleanup for temp test directories. */ }
            catch (UnauthorizedAccessException) { /* Best-effort cleanup for temp test directories. */ }

            try { Directory.Delete(destRoot, true); }
            catch (IOException) { /* Best-effort cleanup for temp test directories. */ }
            catch (UnauthorizedAccessException) { /* Best-effort cleanup for temp test directories. */ }
        }
    }
}

