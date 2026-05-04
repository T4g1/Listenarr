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
using Listenarr.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using System.IO.Compression;
using System.Reflection;
using Listenarr.Tests.Common;
using Listenarr.Domain.Common;
using Listenarr.Tests.Builders;
using Listenarr.Application.Services;
using Listenarr.Domain.Models;
using Listenarr.Application.Models.Configurations;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "CompletedDownloadProcessorTests")]
    [Trait("Area", "CompletedDownloadProcessing")]
    [Trait("Category", "CompletedDownloadProcessor")]
    public class CompletedDownloadProcessorTests : BaseTests
    {
        private readonly static string CLIENT_CONFIG_ID = "dl-client-1";
        private readonly static string DOWNLOAD_COMPLETE_ID = "dl-complete-1";
        private readonly static int AUDIOBOOK_ID = 1;

        private List<string> capturedFiles = [];

        private Mock<IToastService> _toastMock = new();
        private Mock<IDownloadHistoryService> _downloadHistoryMock = new();
        private DownloadClientGatewayMock _downloadClientGatewayMock = new();

        private DownloadClientConfiguration _client = new DownloadClientConfigurationBuilder()
                .WithId(CLIENT_CONFIG_ID)
                .WithName("Slskd")
                .WithType("slskd")
                .WithHost("localhost")
                .WithPort(5030)
                .Build();

        private Audiobook _audiobook = new AudiobookBuilder()
            .WithId(AUDIOBOOK_ID)
            .WithTitle("Seconde Fondation")
            .WithAuthor("Isaac Asimov")
            .WithSeries("Le Cycle de Fondation")
            .Build();

        public override async Task InitializeAsync()
        {
            _toastMock = new Mock<IToastService>();
            _toastMock
                .Setup(t => t.PublishToastAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>()))
                .Returns(Task.CompletedTask);

            _downloadHistoryMock = new Mock<IDownloadHistoryService>();
            _downloadHistoryMock
                .Setup(h => h.RecordImportFailedAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>()))
                .Returns(Task.CompletedTask);

            _services.AddSingleton(_toastMock.Object);
            _services.AddSingleton(_downloadHistoryMock.Object);
            _services.AddSingleton<IDownloadClientGateway>(_downloadClientGatewayMock);
            _services.Replace(new ServiceDescriptor(typeof(IDownloadItemService), typeof(DownloadItemService), ServiceLifetime.Singleton));

            var downloadImportServiceMock = new Mock<IDownloadImportService>();
            downloadImportServiceMock
                .Setup(f => f.ImportFilesFromDirectoryAsync(It.IsAny<Download>(), It.IsAny<Audiobook>(), It.IsAny<IEnumerable<string>>(), It.IsAny<ApplicationSettings>(), It.IsAny<CancellationToken>()))
                .Callback<Download, Audiobook, IEnumerable<string>, ApplicationSettings, CancellationToken>((_, _, files, _, _) => capturedFiles = [.. files])
                .ReturnsAsync((Download _, Audiobook _, IEnumerable<string> files, ApplicationSettings _, CancellationToken _) =>
                    [.. files.Select(f => new ImportResult
                    {
                        Success = true,
                        SourcePath = f,
                        FinalPath = f
                    })]);
            _services.AddSingleton(downloadImportServiceMock.Object);

            Init();

            await InitDataAsync();
        }

        private async Task InitDataAsync()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}-{ChapterNumber:00}")
                .WithoutExtractArchive()
                .WithImportBlacklistExtension(".nfo")
                .Build());

            await _downloadClientConfigurationRepository.SaveAsync(_client);
            await _audiobookRepository.AddAsync(_audiobook);

            await _downloadRepository.AddAsync(new Download
            {
                Id = DOWNLOAD_COMPLETE_ID,
                DownloadClientId = CLIENT_CONFIG_ID,
                AudiobookId = AUDIOBOOK_ID,
                Metadata = new Dictionary<string, object>
                {
                    ["Uploader"] = "USER1",
                    ["Protocol"] = DownloadProtocol.Torrent
                }
            });

            var client = new DownloadClientConfigurationBuilder()
                .WithId("client-cover-path")
                .Build();

            var audiobook = new AudiobookBuilder()
                .WithTitle("Wonderful")
                .WithAuthor("Author")
                .Build();
        }

        [Fact]
        [Trait("Method", "MarkImportFailureAsync")]
        [Trait("Scenario", "TransientFailureStaysImportPending")]
        public async Task MarkImportFailureAsync_FirstAttempt_KeepsImportPendingForRetry()
        {
            var download = await _downloadRepository.AddAsync(new Download
            {
                Status = DownloadStatus.Downloading,
                DownloadClientId = "client-retry",
                Title = "Retry Candidate"
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);

            var markImportFailureMethod = typeof(CompletedDownloadProcessor)
                .GetMethod("MarkImportFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(markImportFailureMethod);

            var task = (Task?)markImportFailureMethod!.Invoke(processor,
            [
                download,
                "TransientFailure",
                "boom",
                null,
                false
            ]);

            Assert.NotNull(task);
            await task!;

            var tracked = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.ImportPending, tracked!.Status);
            Assert.Equal(1, tracked.ImportAttempts);
            Assert.Null(tracked.ImportBlockReason);
            Assert.True(tracked.ImportBlockMessages.Count == 1);
            Assert.Contains("boom", tracked.ImportBlockMessages[0], StringComparison.OrdinalIgnoreCase);
            Assert.Null(tracked.ErrorMessage);

            _downloadHistoryMock.Verify(h => h.RecordImportFailedAsync(
                download.Id,
                "client-retry",
                "Retry Candidate",
                It.Is<string>(msg => msg.Contains("boom", StringComparison.OrdinalIgnoreCase))), Times.Once);

            _toastMock.Verify(t => t.PublishToastAsync(
                "warning",
                "Manual Interaction Required",
                It.IsAny<string>(),
                It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        [Trait("Method", "MarkImportFailureAsync")]
        [Trait("Scenario", "ThresholdFailureBlocksAndSignalsManualInteraction")]
        public async Task MarkImportFailureAsync_ThirdAttempt_BlocksAndSignalsManualInteraction()
        {
            var download = await _downloadRepository.AddAsync(new Download
            {
                Status = DownloadStatus.ImportPending,
                DownloadClientId = "client-threshold",
                Title = "Threshold Candidate",
                ImportAttempts = 2
            });

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);

            var markImportFailureMethod = typeof(CompletedDownloadProcessor)
                .GetMethod("MarkImportFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(markImportFailureMethod);

            var task = (Task?)markImportFailureMethod!.Invoke(processor, new object?[]
            {
                download,
                "RepeatedFailure",
                "still failing",
                null,
                false
            });

            Assert.NotNull(task);
            await task!;

            var tracked = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.ImportBlocked, tracked!.Status);
            Assert.Equal(3, tracked.ImportAttempts);
            Assert.Equal("RepeatedFailure", tracked.ImportBlockReason);
            Assert.NotNull(tracked.ImportBlockMessages);
            Assert.Contains(tracked.ImportBlockMessages!, m => m.Contains("still failing", StringComparison.OrdinalIgnoreCase));

            _downloadHistoryMock.Verify(h => h.RecordImportFailedAsync(
                download.Id,
                "client-threshold",
                "Threshold Candidate",
                It.Is<string>(msg => msg.Contains("still failing", StringComparison.OrdinalIgnoreCase))), Times.Once);

            _toastMock.Verify(t => t.PublishToastAsync(
                "warning",
                "Manual Interaction Required",
                It.Is<string>(msg => msg.Contains("could not be imported automatically", StringComparison.OrdinalIgnoreCase)),
                8000), Times.Once);

            var records = await _historyRepository.GetRecentAsync(1);
            var capturedHistory = Assert.Single(records);
            Assert.NotNull(capturedHistory);
            Assert.Equal("ImportBlocked", capturedHistory!.EventType);
            Assert.Contains("still failing", capturedHistory.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Method", "MarkImportFailureAsync")]
        [Trait("Scenario", "AttemptCounterPersistsAcrossRestart")]
        public async Task MarkImportFailureAsync_AttemptCounterPersistsAcrossProcessorRestartSimulation()
        {
            var download = await _downloadRepository.AddAsync(new Download
            {
                Status = DownloadStatus.ImportPending,
                DownloadClientId = "client-persist",
                Title = "Persistent Attempts",
                ImportAttempts = 1
            });

            var method = typeof(CompletedDownloadProcessor)
                .GetMethod("MarkImportFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var firstProcessor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            var firstTask = (Task?)method!.Invoke(firstProcessor,
            [
                download,
                "TransientFailure",
                "attempt two",
                null,
                false
            ]);
            Assert.NotNull(firstTask);
            await firstTask!;

            var afterFirst = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(afterFirst);
            Assert.Equal(DownloadStatus.ImportPending, afterFirst!.Status);
            Assert.Equal(2, afterFirst.ImportAttempts);

            var restartedProcessor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            var secondTask = (Task?)method!.Invoke(restartedProcessor,
            [
                download,
                "TransientFailure",
                "attempt three",
                null,
                false
            ]);
            Assert.NotNull(secondTask);
            await secondTask!;

            var afterSecond = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(afterSecond);
            Assert.Equal(DownloadStatus.ImportBlocked, afterSecond!.Status);
            Assert.Equal(3, afterSecond.ImportAttempts);
            Assert.Equal("TransientFailure", afterSecond.ImportBlockReason);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "NoImportableFilesBlocksAndSignalsManualInteraction")]
        public async Task ProcessCompletedDownloadAsync_NoImportableFiles_BlocksDownload_AndSignalsManualInteraction()
        {
            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                DownloadClientId = (await CreateDownloadClientConfiguration()).Id,
                AudiobookId = (await CreateAudiobook()).Id,
                Title = "Broken Import"
            };
            await _downloadRepository.AddAsync(download);

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, string.Empty);

            var tracked = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.ImportBlocked, tracked!.Status);
            Assert.Equal("NoImportableFiles", tracked.ImportBlockReason);
            Assert.Equal(1, tracked.ImportAttempts);
            Assert.NotNull(tracked.ImportBlockMessages);
            Assert.Contains(tracked.ImportBlockMessages!, m => m.Contains("Manual interaction is required.", StringComparison.OrdinalIgnoreCase));

            _downloadHistoryMock.Verify(h => h.RecordImportFailedAsync(
                download.Id,
                download.DownloadClientId,
                "Broken Import",
                It.Is<string>(msg => msg.Contains("Manual interaction is required.", StringComparison.OrdinalIgnoreCase))), Times.Once);

            _toastMock.Verify(t => t.PublishToastAsync(
                "warning",
                "Manual Interaction Required",
                It.Is<string>(msg => msg.Contains("could not be imported automatically", StringComparison.OrdinalIgnoreCase)),
                8000), Times.Once);

            var records = await _historyRepository.GetRecentAsync(1);
            var capturedHistory = Assert.Single(records);
            Assert.NotNull(capturedHistory);
            Assert.Equal("ImportBlocked", capturedHistory!.EventType);
            Assert.Contains("Manual interaction is required.", capturedHistory.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "SingleFileImportSetsMovedAndFinalPath")]
        public async Task ProcessCompletedDownloadAsync_SingleFile_UpdatesStatus()
        {
            // Arrange
            var finalPath = await FileService.GetTempFileAsync("audiobook.mp3");

            _downloadClientGatewayMock.SourceFiles = [finalPath];

            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                DownloadClientId = (await CreateDownloadClientConfiguration()).Id,
                AudiobookId = (await CreateAudiobook()).Id
            };
            await _downloadRepository.AddAsync(download);

            // Act
            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, finalPath);

            // Assert
            var tracked = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "DirectoryImportInvokesDirectoryPathFlow")]
        public async Task ProcessCompletedDownloadAsync_Directory_InvokesDirectoryImport()
        {
            // Arrange
            _downloadClientGatewayMock.SourceFiles = [
                await FileService.GetFileAsync(FileService.GetTempPath(), "file1.mp3")
            ];

            var download = new Download
            {
                DownloadClientId = (await CreateDownloadClientConfiguration()).Id,
                AudiobookId = (await CreateAudiobook()).Id,
                // FIXME: This is not a completed download, thus ProcessCompletedDownloadAsync should do nothing on it
                Status = DownloadStatus.Downloading
            };
            await _downloadRepository.AddAsync(download);

            // Act
            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, FileService.GetTempPath());

            // Assert
            var tracked = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "DirectoryImportIncludesCompanionFilesAndRespectsBlacklist")]
        public async Task ProcessCompletedDownloadAsync_Directory_PassesCompanionFilesExceptBlacklisted()
        {
            var audioPath = await FileService.GetFileAsync(FileService.GetTempPath(), "file1.mp3");
            var coverPath = await FileService.GetFileAsync(FileService.GetTempPath(), "cover.jpg");
            var nfoPath = await FileService.GetFileAsync(FileService.GetTempPath(), "release.nfo");
            var archivePath = await FileService.GetFileAsync(FileService.GetTempPath(), "release.zip");

            _downloadClientGatewayMock.SourceFiles = [audioPath, coverPath, nfoPath, archivePath];

            var audiobook = await CreateAudiobook();
            var download = new Download
            {
                DownloadClientId = (await CreateDownloadClientConfiguration()).Id,
                AudiobookId = audiobook.Id,
                // FIXME: This is not a completed download, thus ProcessCompletedDownloadAsync should do nothing on it
                Status = DownloadStatus.Downloading
            };

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithoutExtractArchive()
                .WithImportBlacklistExtension(".nfo")
                .Build());

            await _downloadRepository.AddAsync(download);

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, FileService.GetTempPath());

            Assert.NotNull(capturedFiles);
            Assert.Contains(audioPath, capturedFiles!);
            Assert.Contains(coverPath, capturedFiles!);
            Assert.DoesNotContain(nfoPath, capturedFiles!);
            Assert.DoesNotContain(archivePath, capturedFiles!);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "DirectoryImportSeparatesMixedAudiobooksByTitle")]
        public async Task ProcessCompletedDownloadAsync_Directory_FiltersMixedAudioFilesToMatchingTitle()
        {
            var targetAudioPath = await FileService.GetTempFileAsync("Target Book.m4b");
            var foreignAudioPath = await FileService.GetTempFileAsync("Different Book.m4b");
            var coverPath = await FileService.GetTempFileAsync("cover.jpg");

            _downloadClientGatewayMock.SourceFiles = [targetAudioPath, coverPath];

            var audiobook = await CreateAudiobook();
            var download = new Download
            {
                DownloadClientId = (await CreateDownloadClientConfiguration()).Id,
                AudiobookId = audiobook.Id,
                Status = DownloadStatus.Downloading,
                Title = "Target Book"
            };
            await _downloadRepository.AddAsync(download);

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, FileService.GetTempPath());

            Assert.NotNull(capturedFiles);
            Assert.Contains(targetAudioPath, capturedFiles!);
            Assert.Contains(coverPath, capturedFiles!);
            Assert.DoesNotContain(foreignAudioPath, capturedFiles!);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "DirectoryImportUsesClientReportedFilesAsSourceOfTruth")]
        public async Task ProcessCompletedDownloadAsync_Directory_UsesClientReportedFilesToIncludeOnlyTrackedFiles()
        {
            var firstAudioPath = await FileService.GetTempFileAsync("Alpha Book.m4b");
            var secondAudioPath = await FileService.GetTempFileAsync("Omega Companion.m4b");
            var txtPath = await FileService.GetTempFileAsync("book.txt");
            var unrelatedPath = await FileService.GetTempFileAsync("unrelated.jpg");

            _downloadClientGatewayMock.SourceFiles = [
                firstAudioPath,
                secondAudioPath,
                txtPath
            ];

            var audiobook = await CreateAudiobook();
            var download = new Download
            {
                Status = DownloadStatus.Downloading,
                DownloadClientId = (await CreateDownloadClientConfiguration()).Id,
                AudiobookId = audiobook.Id,
                Title = "Alpha Book",
                Metadata = new Dictionary<string, object>
                {
                    ["TorrentHash"] = "ABC123"
                }
            };

            await _downloadRepository.AddAsync(download);

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, FileService.GetTempPath());

            Assert.NotNull(capturedFiles);
            Assert.Contains(firstAudioPath, capturedFiles!);
            Assert.Contains(secondAudioPath, capturedFiles!);
            Assert.Contains(txtPath, capturedFiles!);
            Assert.DoesNotContain(unrelatedPath, capturedFiles!);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "NonAudioSingleFileFallsBackToDirectoryImport")]
        public async Task ProcessCompletedDownloadAsync_NonAudioSingleFile_UsesParentDirectoryWhenAudioExists()
        {
            var audioPath = await FileService.GetTempFileAsync("book.m4b");
            var coverPath = await FileService.GetTempFileAsync("book.jpg");
            _downloadClientGatewayMock.SourceFiles = [audioPath, coverPath];

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithStatus(DownloadStatus.Downloading)
                .WithDownloadClientConfiguration(_client)
                .WithAudiobook(await CreateAudiobook())
                .Build());

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, coverPath);

            Assert.NotNull(capturedFiles);
            Assert.Contains(audioPath, capturedFiles!);
            Assert.Contains(coverPath, capturedFiles!);

            var tracked = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.Moved, tracked!.Status);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "RecursiveDirectoryImportsNestedFile")]
        public async Task ProcessCompletedDownloadAsync_RecursiveDirectory_ImportsNestedFile()
        {
            var nested = FileService.GetTempDirectory("nested");
            var filePath = await FileService.GetFileAsync(nested, "file2.mp3");
            _downloadClientGatewayMock.SourceFiles = [filePath];

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithStatus(DownloadStatus.Downloading)
                .WithDownloadClientConfiguration(_client)
                .WithAudiobook(await CreateAudiobook())
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(FileService.GetTempPath())
                .Build());

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, FileService.GetTempPath());

            var tracked = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
        }

        [Fact]
        [Trait("Scenario", "ArchiveExtractionImportsContainedFile")]
        public async Task ProcessCompletedDownloadAsync_ArchiveExtraction_ImportsContainedFile()
        {
            _services.Replace(new ServiceDescriptor(typeof(IDownloadImportService), typeof(DownloadImportService), ServiceLifetime.Singleton));
            Init();
            await InitDataAsync();

            var destinationDirectory = FileService.GetTempDirectory("destination");
            var inner = FileService.GetTempDirectory("inner");
            _ = await FileService.GetFileAsync(inner, "audio.mp3");
            var zipPath = Path.Join(FileService.GetTempPath(), "release.zip");
            ZipFile.CreateFromDirectory(inner, zipPath);
            Assert.True(File.Exists(zipPath));

            _downloadClientGatewayMock.SourceFiles = [zipPath];

            var audiobook = await CreateAudiobook();
            audiobook.BasePath = Path.Join(destinationDirectory, "Fake Author/Fake Title/Anything Really");
            await _audiobookRepository.UpdateAsync(audiobook);

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithStatus(DownloadStatus.Downloading)
                .WithDownloadClientConfiguration(_client)
                .WithPath(FileService.GetTempPath())
                .WithAudiobook(audiobook)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithExtractArchive()
                .WithMultiFileNamingPattern("{Title}")
                .WithOutputPath(destinationDirectory)
                .WithoutMetadataProcessing()
                .Build());

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, zipPath);

            var tracked = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(tracked);
            Assert.True(tracked!.Status == DownloadStatus.Completed || tracked.Status == DownloadStatus.Moved, $"Expected Completed or Moved, got {tracked.Status}");
            Assert.True(File.Exists(Path.Join(audiobook.BasePath, "audio.mp3")));
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        [Trait("Scenario", "InvalidTransitionIsRejected")]
        public async Task InvalidTransition_IsRejectedAndLogged()
        {
            var queueMock = new Mock<IDownloadQueueService>();

            _services.AddSingleton(queueMock.Object);
            Init();
            await InitDataAsync();

            var download = new Download
            {
                AudiobookId = (await CreateAudiobook()).Id,
                DownloadClientId = _client.Id,
                Status = DownloadStatus.Moved,
                Title = "Already Imported"
            };
            await _downloadRepository.AddAsync(download);

            var processor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await processor.ProcessCompletedDownloadAsync(download, FileUtils.GetAbsolutePath("temp", "should-not-run.m4b"));

            var tracked = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(tracked);
            Assert.Equal(DownloadStatus.Moved, tracked!.Status);

            queueMock.Verify(q => q.GetQueueAsync(), Times.Never);
        }

        [Fact]
        [Trait("Method", "ProcessCompletedDownloadAsync")]
        public async Task ProcessCompleteDownloadAsync_MultipleFiles()
        {
            _services.Replace(new ServiceDescriptor(typeof(IDownloadImportService), typeof(DownloadImportService), ServiceLifetime.Singleton));
            Init();
            await InitDataAsync();

            var localSource = FileService.GetTempDirectory("dl-local-source");
            var localDestination = FileService.GetTempDirectory("dl-destination");

            var localChapter1 = await FileService.GetFileAsync(localSource, "01 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter2 = await FileService.GetFileAsync(localSource, "02 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter3 = await FileService.GetFileAsync(localSource, "03 - Seconde Fondation Isaac Asimov.mp3");
            var localChapter4 = await FileService.GetFileAsync(localSource, "04 - Seconde Fondation Isaac Asimov.mp3");
            var localCompanion = await FileService.GetFileAsync(localSource, "Seconde Fondation Isaac Asimov.nfo");

            _downloadClientGatewayMock.SourceFiles = [localChapter1, localChapter2, localChapter3, localChapter4, localCompanion];

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithMultiFileNamingPattern("{Title}-{DiskNumber:00}-{ChapterNumber:00}")
                .WithImportBlacklistExtension(".nfo")
                .Build());

            var download = (await _downloadRepository.GetByIdsAsync([DOWNLOAD_COMPLETE_ID])).First();

            var basePath = Path.Join(localDestination, "Isaac Asimov", "Le Cycle de Fondation", "Seconde Fondation");

            _audiobook.PublishYear = "1996";
            _audiobook.BasePath = basePath;
            await _audiobookRepository.UpdateAsync(_audiobook);

            var completeDownloadProcessor = MockUtils.CreateCompletedDownloadProcessor(_provider);
            await completeDownloadProcessor.ProcessCompletedDownloadAsync(download, localSource);

            download = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.Moved, download.Status);

            var files = await _audiobookFileRepository.GetAllAsync();
            Assert.Equal(4, files.Count);

            // FIXME: disc and track number are the same because ffprobe metadata are not used (see ImportService.ImportFilesFromDirectoryAsync)
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-01-01.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-02-02.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-03-03.mp3")));
            Assert.True(File.Exists(Path.Join(basePath, "Seconde Fondation-04-04.mp3")));
            Assert.False(File.Exists(Path.Join(basePath, "Seconde Fondation Isaac Asimov.nfo")));
        }
    }
}
