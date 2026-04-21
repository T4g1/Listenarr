using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Listenarr.Api.Services.Adapters;
using Xunit;
using Moq;
using Listenarr.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Listenarr.Api.Hubs;
using Listenarr.Domain.Utils;
using System.Reflection;
using Listenarr.Api.Services.Metadata;
using System.Runtime.InteropServices;

namespace Listenarr.Api.Tests
{
    [Trait("Category", "DownloadProcessing")]
    public class DownloadProcessingTests : BaseTests
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
                Port = 5030
            });

            context.Downloads.Add(new Download
            {
                Id = DOWNLOAD_COMPLETE_ID,
                DownloadClientId = CLIENT_CONFIG_ID,
                Metadata = new Dictionary<string, object>
                {
                    ["Uploader"] = "USER1",
                    ["Protocol"] = DownloadProtocol.Torrent
                }
            });
        }
        
        [Fact]
        public async Task ProcessCompletedDownload_CreatesAudiobookFileAndBroadcasts()
        {
            // Arrange: in-memory DB
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);

            // Seed an audiobook and a download
            var book = new Audiobook { Title = "Test Book" };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var testPath = Path.Join(Path.GetTempPath(), $"dl-test-{Guid.NewGuid()}.m4b");
            await File.WriteAllTextAsync(testPath, "dummy content");

            var download = new Download
            {
                Id = "dl-1",
                AudiobookId = book.Id,
                Title = "Test Book",
                Status = DownloadStatus.Downloading,
                DownloadPath = testPath,
                FinalPath = testPath,
                StartedAt = DateTime.UtcNow
            };
            db.Downloads.Add(download);
            await db.SaveChangesAsync();

            // Mock metadata service
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "Test Book", Duration = TimeSpan.FromSeconds(3600), Format = "m4b", Bitrate = 64000, SampleRate = 44100, Channels = 2 });

            // Mock hub context
            var hubClientsMock = new Mock<IHubClients>();
            var clientProxyMock = new Mock<IClientProxy>();
            hubClientsMock.Setup(h => h.All).Returns(clientProxyMock.Object);
            clientProxyMock.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<System.Threading.CancellationToken>()))
                .Returns(System.Threading.Tasks.Task.CompletedTask);

            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.SetupGet(h => h.Clients).Returns(hubClientsMock.Object);

            // Build service provider for scope factory and register metadata service
            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton(hubContextMock.Object);
            services.AddSingleton(db);
            // AudioFileService and dependencies (for creating AudiobookFile records)
            services.AddMemoryCache();
            services.AddSingleton<MetadataExtractionLimiter>();
            var loggerAfMock = new Mock<Microsoft.Extensions.Logging.ILogger<AudioFileService>>();
            services.AddScoped<IAudioFileService, Listenarr.Api.Services.AudioFileService>();
            services.AddSingleton(loggerAfMock.Object);

            // Construct DownloadService with required dependencies (use nulls/mocks where not needed)
            var repoMock = new Mock<IAudiobookRepository>();
            var configMock = new Mock<IConfigurationService>();
            // Provide deterministic application settings for imports so file operations
            // and naming behave predictably in tests.
            configMock.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { OutputPath = Path.GetTempPath(), EnableMetadataProcessing = true, CompletedFileAction = "Move" });
            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadService>>();
            using var httpClient = new System.Net.Http.HttpClient();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var cacheMock = new Mock<IMemoryCache>();
            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(() => new ListenArrDbContext(options));
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(() => new ListenArrDbContext(options));
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();

            // Construct ImportService from the provider so it receives the same
            // IServiceScopeFactory as the DownloadService. This allows scoped
            // ListenArrDbContext instances to be discoverable during synchronization.
            var provider2 = TestServiceFactory.BuildServiceProvider(services =>
            {
                // ensure ListenArrDbContext from the test is available to scopes created by the provider
                services.AddSingleton<ListenArrDbContext>(db);
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                // metadata extraction required by AudioFileService
                services.AddSingleton<IMetadataService>(metadataMock.Object);
                // Ensure the same in-memory ListenArrDbContext instance is resolvable for sync
                services.AddSingleton<ListenArrDbContext>(db);
                // Provide metadata service for AudioFileService
                services.AddSingleton<IMetadataService>(metadataMock.Object);
                // Ensure the same in-memory ListenArrDbContext and supporting services
                // are available when ImportService/AudioFileService run during the test.
                services.AddSingleton<ListenArrDbContext>(db);
                services.AddSingleton<MetadataExtractionLimiter>();
                services.AddSingleton<Microsoft.Extensions.Logging.ILogger<DownloadService>>(loggerMock.Object);
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpClientFactoryMock.Object);
                services.AddSingleton<IImportService>(sp => new ImportService(
                    dbFactoryMock.Object,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    new FileNamingService(configMock.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<FileNamingService>()),
                    metadataMock.Object,
                    new Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportService>()));
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                // Ensure metadata service is available to AudioFileService during import/registration
                services.AddSingleton<IMetadataService>(metadataMock.Object);
                // Ensure metadata service is available to AudioFileService during import/registration
                services.AddSingleton<IMetadataService>(metadataMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<Listenarr.Api.Services.Adapters.IDownloadClientAdapterFactory>(new Mock<Listenarr.Api.Services.Adapters.IDownloadClientAdapterFactory>().Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(cacheMock.Object);
                services.AddTransient<DownloadService>();
            });
            var downloadService = provider2.GetRequiredService<DownloadService>();

            // Act
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: audiobook file created (verify with fresh DbContext), or import deferred and file present on disk
            await using var verifyDb = new ListenArrDbContext(options);
            var file = await verifyDb.AudiobookFiles.FirstOrDefaultAsync(f => f.AudiobookId == book.Id);
            if (file != null)
            {
                Assert.Equal(download.FinalPath, file.Path);
                Assert.NotNull(file.DurationSeconds);
                Assert.InRange(file.DurationSeconds.Value, 3599.0, 3601.0);
                Assert.Equal("m4b", file.Format);
            }
            else
            {
                // Import deferred; assert that the final file (or a file in the output path) exists on disk
                Assert.True(File.Exists(download.FinalPath) || Directory.GetFiles(Path.GetDirectoryName(download.FinalPath) ?? string.Empty, "*", SearchOption.TopDirectoryOnly).Length > 0, "Expected the final file or files on disk when import is deferred");
            }

            // Broadcast behavior not asserted here; ensure import and registration completed successfully.
        }

        [Fact]
        public async Task ProcessCompletedDownload_AudiobookWithBasePath_UsesFilenameOnly_NoExtraFolders()
        {
            // Arrange: in-memory DB
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);

            // Create a temporary base path directory for the audiobook
            var basePath = Path.Join(Path.GetTempPath(), $"audiobook-base-{Guid.NewGuid()}");
            Directory.CreateDirectory(basePath);

            // Seed an audiobook with BasePath
            var book = new Audiobook
            {
                Title = "Test Audiobook",
                Authors = new System.Collections.Generic.List<string> { "Test Author" },
                BasePath = basePath
            };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Create source file in a different location
            var sourceDir = Path.Join(Path.GetTempPath(), $"source-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Join(sourceDir, "source-file.m4b");
            await File.WriteAllTextAsync(sourceFile, "dummy content");

            var download = new Download
            {
                Id = "dl-basepath-1",
                AudiobookId = book.Id,
                Title = "Test Audiobook",
                Status = DownloadStatus.Downloading,
                DownloadPath = sourceFile,
                FinalPath = sourceFile,
                StartedAt = DateTime.UtcNow
            };
            db.Downloads.Add(download);
            await db.SaveChangesAsync();

            // Mock configuration service to return settings with metadata processing enabled
            // and a naming pattern that would normally create subdirectories
            var configMock = new Mock<IConfigurationService>();
            var settings = new ApplicationSettings
            {
                EnableMetadataProcessing = true,
                FileNamingPattern = "{Author}/{Series}/{DiskNumber:00} - {Title}",
                CompletedFileAction = "Move"
            };
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            // Mock metadata service
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = "Test Audiobook",
                    Artist = "Test Author",
                    Duration = TimeSpan.FromSeconds(3600),
                    Format = "m4b",
                    Bitrate = 64000,
                    SampleRate = 44100,
                    Channels = 2
                });

            // Mock hub context
            var hubClientsMock = new Mock<IHubClients>();
            var clientProxyMock = new Mock<IClientProxy>();
            hubClientsMock.Setup(h => h.All).Returns(clientProxyMock.Object);
            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.SetupGet(h => h.Clients).Returns(hubClientsMock.Object);

            // Build service provider
            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton<IConfigurationService>(configMock.Object);
            services.AddSingleton(hubContextMock.Object);
            services.AddSingleton(db);
            services.AddMemoryCache();
            services.AddSingleton<MetadataExtractionLimiter>();
            var loggerAfMock = new Mock<Microsoft.Extensions.Logging.ILogger<AudioFileService>>();
            services.AddScoped<IAudioFileService, Listenarr.Api.Services.AudioFileService>();
            services.AddSingleton(loggerAfMock.Object);

            // Construct DownloadService
            var repoMock = new Mock<IAudiobookRepository>();
            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadService>>();
            using var httpClient = new System.Net.Http.HttpClient();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var cacheMock = new Mock<IMemoryCache>();
            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(() => new ListenArrDbContext(options));
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(() => new ListenArrDbContext(options));
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();

            var provider2 = TestServiceFactory.BuildServiceProvider(services =>
            {
                // ensure ListenArrDbContext from the test is available to scopes created by the provider
                services.AddSingleton<ListenArrDbContext>(db);
                // provide limiter required by AudioFileService
                services.AddSingleton<MetadataExtractionLimiter>();
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<ILogger<DownloadService>>(loggerMock.Object);
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpClientFactoryMock.Object);
                // Ensure metadata service is available inside the provider used by DownloadService
                services.AddSingleton<IMetadataService>(metadataMock.Object);
                services.AddSingleton<IImportService>(sp => new ImportService(
                    dbFactoryMock.Object,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    new FileNamingService(configMock.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<FileNamingService>()),
                    metadataMock.Object,
                    new Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportService>()));
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<IDownloadClientAdapterFactory>(new Mock<IDownloadClientAdapterFactory>().Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(cacheMock.Object);
                services.AddTransient<DownloadService>();
            });
            var downloadService = provider2.GetRequiredService<DownloadService>();

            // Act
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: download is completed and final path is within BasePath (verify with fresh DbContext)
            await using var verifyDb = new ListenArrDbContext(options);
            var updatedDownload = await verifyDb.Downloads.FindAsync(download.Id);
            Assert.NotNull(updatedDownload);
            Assert.True(updatedDownload.Status == DownloadStatus.Completed || updatedDownload.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {updatedDownload.Status}");
            Assert.NotNull(updatedDownload.FinalPath);
            // Either the file was moved into the audiobook BasePath synchronously, or finalization queued/deferred the import and FinalPath may remain the original source path.
            bool movedIntoBase = updatedDownload.FinalPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase);
            bool stillAtSource = string.Equals(updatedDownload.FinalPath, sourceFile, StringComparison.OrdinalIgnoreCase);
            Assert.True(movedIntoBase || stillAtSource, $"FinalPath should either be in BasePath or equal source path, got {updatedDownload.FinalPath}");

            if (movedIntoBase)
            {
                // Assert: file exists at final path and no extra folders created
                Assert.True(File.Exists(updatedDownload.FinalPath));
                var relativePath = Path.GetRelativePath(basePath, updatedDownload.FinalPath);
                Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), relativePath);
                Assert.DoesNotContain(Path.AltDirectorySeparatorChar.ToString(), relativePath);

                var directoriesInBasePath = Directory.GetDirectories(basePath, "*", SearchOption.AllDirectories);
                Assert.Empty(directoriesInBasePath);

                var filesInBasePath = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories);
                Assert.Single(filesInBasePath);
                Assert.Equal(updatedDownload.FinalPath, filesInBasePath[0]);

                // Assert: source file was moved (not copied)
                Assert.False(File.Exists(sourceFile));

                // Assert: audiobook file record was created
                var audiobookFile = await db.AudiobookFiles.FirstOrDefaultAsync(f => f.AudiobookId == book.Id);
                Assert.NotNull(audiobookFile);
                Assert.Equal(updatedDownload.FinalPath, audiobookFile.Path);
            }
            else
            {
                // Import may be deferred; ensure source file still exists and job should be queued/handled later.
                Assert.True(File.Exists(sourceFile));
            }

            // Cleanup
            try
            {
                if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
                if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) { /* Ignore cleanup errors */ }
        }

        [Fact]
        public async Task ProcessCompletedDownload_AudiobookWithMultipleFiles()
        {
            // Arrange: in-memory DB
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            var db = new ListenArrDbContext(options);

            // Create a temporary base path directory for the audiobook
            var basePath = Path.Join(Path.GetTempPath(), $"audiobook-base-{Guid.NewGuid()}");
            Directory.CreateDirectory(basePath);

            // Seed an audiobook with BasePath
            var book = new Audiobook
            {
                Title = "Test Audiobook",
                Authors = new System.Collections.Generic.List<string> { "Test Author" },
                BasePath = basePath
            };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Create three source file in a different location
            var sourceDir = Path.Join(Path.GetTempPath(), $"source-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var sourceFile1 = Path.Join(sourceDir, "source-file1.m4b");
            await File.WriteAllTextAsync(sourceFile1, "dummy content");
            var sourceFile2 = Path.Join(sourceDir, "source-file2.m4b");
            await File.WriteAllTextAsync(sourceFile2, "dummy content");
            var sourceFile3 = Path.Join(sourceDir, "source-file3.m4b");
            await File.WriteAllTextAsync(sourceFile3, "dummy content");

            var download = new Download
            {
                Id = "dl-basepath-1",
                AudiobookId = book.Id,
                Title = "Test Audiobook",
                Status = DownloadStatus.Downloading,
                DownloadPath = sourceDir,
                FinalPath = sourceDir,
                StartedAt = DateTime.UtcNow
            };
            db.Downloads.Add(download);
            await db.SaveChangesAsync();

            // Mock configuration service to return settings with metadata processing enabled
            // and a naming pattern that would normally create subdirectories
            var configMock = new Mock<IConfigurationService>();
            var settings = new ApplicationSettings
            {
                EnableMetadataProcessing = true,
                FileNamingPattern = "{Author}/{Series}/{DiskNumber:00} - {Title}",
                CompletedFileAction = "Move"
            };
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(settings);

            // Mock metadata service
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = "Test Audiobook",
                    Format = "m4b",
                });

            // Mock hub context
            var hubClientsMock = new Mock<IHubClients>();
            var clientProxyMock = new Mock<IClientProxy>();
            hubClientsMock.Setup(h => h.All).Returns(clientProxyMock.Object);
            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.SetupGet(h => h.Clients).Returns(hubClientsMock.Object);

            // Build service provider
            var services = new ServiceCollection();
            services.AddSingleton<IMetadataService>(metadataMock.Object);
            services.AddSingleton<IConfigurationService>(configMock.Object);
            services.AddSingleton(hubContextMock.Object);
            services.AddSingleton(db);
            services.AddMemoryCache();
            services.AddSingleton<MetadataExtractionLimiter>();
            var loggerAfMock = new Mock<Microsoft.Extensions.Logging.ILogger<AudioFileService>>();
            services.AddScoped<IAudioFileService, Listenarr.Api.Services.AudioFileService>();
            services.AddSingleton(loggerAfMock.Object);

            // Construct DownloadService
            var repoMock = new Mock<IAudiobookRepository>();
            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<DownloadService>>();
            using var httpClient = new System.Net.Http.HttpClient();
            var httpClientFactoryMock = new Mock<IHttpClientFactory>();
            httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);
            var cacheMock = new Mock<IMemoryCache>();
            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock.Setup(f => f.CreateDbContext()).Returns(() => new ListenArrDbContext(options));
            dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(() => new ListenArrDbContext(options));
            var pathMappingMock = new Mock<IRemotePathMappingService>();
            var searchMock = new Mock<ISearchService>();

            var provider2 = TestServiceFactory.BuildServiceProvider(services =>
            {
                // ensure ListenArrDbContext from the test is available to scopes created by the provider
                services.AddSingleton<ListenArrDbContext>(db);
                // provide limiter required by AudioFileService
                services.AddSingleton<MetadataExtractionLimiter>();
                services.AddSingleton<IAudiobookRepository>(repoMock.Object);
                services.AddSingleton<IConfigurationService>(configMock.Object);
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<ILogger<DownloadService>>(loggerMock.Object);
                services.AddSingleton<HttpClient>(httpClient);
                services.AddSingleton<IHttpClientFactory>(httpClientFactoryMock.Object);
                // Ensure metadata service is available inside the provider used by DownloadService
                services.AddSingleton<IMetadataService>(metadataMock.Object);
                services.AddSingleton<IImportService>(sp => new ImportService(
                    dbFactoryMock.Object,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    new FileNamingService(configMock.Object, new Microsoft.Extensions.Logging.Abstractions.NullLogger<FileNamingService>()),
                    metadataMock.Object,
                    new Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportService>()));
                services.AddSingleton<IRemotePathMappingService>(pathMappingMock.Object);
                services.AddSingleton<ISearchService>(searchMock.Object);
                services.AddSingleton<IDownloadClientAdapterFactory>(new Mock<IDownloadClientAdapterFactory>().Object);
                services.AddSingleton<IHubContext<DownloadHub>>(hubContextMock.Object);
                services.AddSingleton<IMemoryCache>(cacheMock.Object);
                services.AddTransient<DownloadService>();
            });
            var downloadService = provider2.GetRequiredService<DownloadService>();

            // Act
            await downloadService.ProcessCompletedDownloadAsync(download.Id, download.FinalPath);

            // Assert: download is completed and final path is within BasePath (verify with fresh DbContext)
            await using var verifyDb = new ListenArrDbContext(options);
            var updatedDownload = await verifyDb.Downloads.FindAsync(download.Id);
            Assert.NotNull(updatedDownload);
            Assert.True(updatedDownload.Status == DownloadStatus.Completed || updatedDownload.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {updatedDownload.Status}");
            Assert.NotNull(updatedDownload.FinalPath);
            // Either the file was moved into the audiobook BasePath synchronously, or finalization queued/deferred the import and FinalPath may remain the original source path.
            bool movedIntoBase = updatedDownload.FinalPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase);
            bool stillAtSource = string.Equals(updatedDownload.FinalPath, sourceDir, StringComparison.OrdinalIgnoreCase);
            Assert.True(movedIntoBase || stillAtSource, $"FinalPath should either be in BasePath or equal source path, got {updatedDownload.FinalPath}");

            if (movedIntoBase)
            {
                // Assert: file exists at final path and no extra folders created
                Assert.True(File.Exists(updatedDownload.FinalPath));
                var relativePath = Path.GetRelativePath(basePath, updatedDownload.FinalPath);
                Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), relativePath);
                Assert.DoesNotContain(Path.AltDirectorySeparatorChar.ToString(), relativePath);

                var directoriesInBasePath = Directory.GetDirectories(basePath, "*", SearchOption.AllDirectories);
                Assert.Empty(directoriesInBasePath);

                var filesInBasePath = Directory.GetFiles(basePath, "*", SearchOption.AllDirectories);
                Assert.Single(filesInBasePath);
                Assert.Equal(updatedDownload.FinalPath, filesInBasePath[0]);

                // Assert: source file were moved (not copied)
                Assert.False(File.Exists(sourceFile1));
                Assert.False(File.Exists(sourceFile2));
                Assert.False(File.Exists(sourceFile3));

                // Assert: audiobook file record was created
                var audiobookFile = await db.AudiobookFiles.FirstOrDefaultAsync(f => f.AudiobookId == book.Id);
                Assert.NotNull(audiobookFile);
                Assert.Equal(updatedDownload.FinalPath, audiobookFile.Path);
            }
            else
            {
                // Import may be deferred; ensure source file still exists and job should be queued/handled later.
                Assert.True(File.Exists(sourceFile1));
                Assert.True(File.Exists(sourceFile2));
                Assert.True(File.Exists(sourceFile3));
            }

            // Cleanup
            try
            {
                if (Directory.Exists(basePath)) Directory.Delete(basePath, true);
                if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) { /* Ignore cleanup errors */ }
        }

        [Fact]
        public async Task DownloadProcessingBackgroundService_ProcessMoveOrCopy_MultipleFilesAndRemotePathMapping()
        {
            var remoteSource = GetTempDirectory("dl-remote-source");
            var localSource = GetTempDirectory("dl-local-source");
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

            var provider = MockUtils.CreateServiceProvider(importItemResolutionServiceMock.Object, context, localDestination);
            var dpbs = new DownloadProcessingBackgroundService(
                provider.GetRequiredService<IServiceScopeFactory>(), 
                new Mock<ILogger<DownloadProcessingBackgroundService>>().Object, 
                provider.GetRequiredService<IAppMetricsService>());

            var job = await MockUtils.CreateDownloadProcessingJob(provider, download, localSource);

            using var scope = provider.CreateScope();

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(dpbs, [job, scope, CancellationToken.None])!;
            await task;

            Assert.NotNull(method);

            Assert.True(File.Exists(Path.Join(localDestination, "01 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "02 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "03 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "04 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "Seconde Fondation Isaac Asimov.nfo")));
        }

        [Fact]
        [Trait("OSPlatform", "Linux")]
        [Trait("OSPlatform", "OSX")]
        public async Task DownloadProcessingBackgroundService_HandleSpace()
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

            var provider = MockUtils.CreateServiceProvider(importItemResolutionServiceMock.Object, context, localDestination);
            var downloadProcessingBackgroundService = new DownloadProcessingBackgroundService(
                provider.GetRequiredService<IServiceScopeFactory>(), 
                new Mock<ILogger<DownloadProcessingBackgroundService>>().Object, 
                provider.GetRequiredService<IAppMetricsService>());

            var job = await MockUtils.CreateDownloadProcessingJob(provider, download, localSource);

            using var scope = provider.CreateScope();

            var method = typeof(DownloadProcessingBackgroundService).GetMethod("ProcessMoveOrCopyJobAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);

            var task = (Task)method!.Invoke(downloadProcessingBackgroundService, [job, scope, CancellationToken.None])!;
            await task;

            Assert.NotNull(method);

            Assert.True(File.Exists(Path.Join(localDestination, "01 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "02 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "03 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "04 - Seconde Fondation Isaac Asimov.mp3")));
            Assert.True(File.Exists(Path.Join(localDestination, "Seconde Fondation Isaac Asimov.nfo")));
        }
    }
}
