using System.Reflection;
using System.Runtime.InteropServices;
using Listenarr.Api.Services;
using Listenarr.Domain.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    [Trait("Category", "DownloadProcessingBackgroundService")]
    public class DownloadProcessingBackgroundServiceTests : BaseTests
    {
        private readonly string CLIENT_CONFIG_ID = "dl-client-1";
        private readonly string DOWNLOAD_COMPLETE_ID = "dl-complete-1";

        private void InitDB(ListenArrDbContext context)
        {
            context.DownloadClientConfigurations.Add(new DownloadClientConfiguration
            {
                Id = CLIENT_CONFIG_ID,
                Name = "Slskd",
                Type = "slskd",
                Host = "localhost",
                Port = 5030,
                IsEnabled = true
            });

            context.Downloads.Add(new Download
            {
                Id = DOWNLOAD_COMPLETE_ID,
                DownloadClientId = CLIENT_CONFIG_ID,
                Metadata = new Dictionary<string, object>
                {
                    ["Uploader"] = "USER1",
                    ["Protocol"] = DownloadProtocol.Torrent
                },
                Status = DownloadStatus.Completed
            });
        }

        [Fact]
        [Trait("Method", "EnqueueCompletedDownloadsAsync")]
        [Trait("Scenario", "Space in directory where downloaded files are located")]
        [Trait("OSPlatform", "Linux")]
        [Trait("OSPlatform", "OSX")]
        public async Task EnqueueCompletedDownloadsAsync_SpaceInFinalPath()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return;
            }
            
            var remoteSource = GetTempDirectory("dl-remote-source ");
            var localSource = GetTempDirectory("dl-local-source ");
            var localDestination = GetTempDirectory("dl-destination");

            var remoteChapter1 = Path.Join(remoteSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter2 = Path.Join(remoteSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter3 = Path.Join(remoteSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var remoteChapter4 = Path.Join(remoteSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var remoteCompanion = Path.Join(remoteSource, "Seconde Fondation Isaac Asimov.nfo");

            var localChapter1 = await GetFileAsync(localSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter2 = await GetFileAsync(localSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter3 = await GetFileAsync(localSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter4 = await GetFileAsync(localSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var localCompanion = await GetFileAsync(localSource, "Seconde Fondation Isaac Asimov.nfo");

            var db = CreateDB(InitDB);
            var context = db.CreateDbContext();
            var client = context.DownloadClientConfigurations.First(c => c.Id == CLIENT_CONFIG_ID);
            var download = context.Downloads.First(d => d.Id == DOWNLOAD_COMPLETE_ID);
            download.FinalPath = remoteSource;
            context.Downloads.Update(download);
            context.SaveChanges();

            context.RemotePathMappings.Add(new RemotePathMapping
            {
                Id = 1,
                DownloadClientId = CLIENT_CONFIG_ID,
                Name = "TEST_REMOTE_MAPPING",
                RemotePath = remoteSource,
                LocalPath = localSource
            });
            await context.SaveChangesAsync();

            var importItemResolutionServiceMock = new Mock<IImportItemResolutionService>();
            importItemResolutionServiceMock
                .Setup(r => r.ResolveImportItemAsync(
                    It.Is<Download>(d => d.Id == download.Id),
                    It.IsAny<QueueItem>(),
                    It.IsAny<QueueItem?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download download, QueueItem queueItem, QueueItem? previousAttempt, CancellationToken ct) =>
                {
                    queueItem.SourceFiles = new List<string> {
                        remoteChapter1,
                        remoteChapter2,
                        remoteChapter3,
                        remoteChapter4,
                        remoteCompanion
                    };
                    return queueItem;
                });

            var provider = MockUtils.CreateServiceProvider(importItemResolutionServiceMock.Object, context, localDestination, client);
            var downloadProcessingBackgroundService = new DownloadProcessingBackgroundService(
                provider.GetRequiredService<IServiceScopeFactory>(), 
                new Mock<ILogger<DownloadProcessingBackgroundService>>().Object, 
                provider.GetRequiredService<IAppMetricsService>());

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("EnqueueCompletedDownloadsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var task = (Task?)method!.Invoke(downloadProcessingBackgroundService, [CancellationToken.None]);
            Assert.NotNull(task);
            await task!;

            Assert.Equal(1, context.DownloadProcessingJobs.Count());
            var job = context.DownloadProcessingJobs.Single();
            Assert.NotNull(job);
            Assert.Equal(localSource, job.SourcePath);
        }
    }
}
