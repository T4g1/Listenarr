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
using System.Reflection;
using Listenarr.Api.Services;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Models.Configurations;
using Listenarr.Application.Models.Enumerations;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class DownloadMonitorPipelineClientCoverageTests : BaseTests
    {
        private static ListenArrDbContext CreateInMemoryDb()
        {
            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new ListenArrDbContext(options);
        }

        [Theory]
        [InlineData("qbittorrent")]
        [InlineData("transmission")]
        [InlineData("sabnzbd")]
        [InlineData("nzbget")]
        public async Task FinalizeDownload_QueuesImport_ForAllSupportedClientTypes(string clientType)
        {
            var sourceDir = FileService.GetTempDirectory("listenarr-pipeline");
            var sourceFile = await FileService.GetFileAsync(sourceDir, "Pipeline Coverage.m4b");

            var outputDir = FileService.GetTempDirectory("listenarr-pipeline-out");

            var queuedSource = string.Empty;
            var queueMock = new Mock<IDownloadProcessingQueueService>();
            queueMock.Setup(q => q.GetJobsForDownloadAsync(It.IsAny<string>())).ReturnsAsync([]);
            queueMock.Setup(q => q.QueueDownloadProcessingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string, string>((downloadId, sourcePath, clientId) => queuedSource = sourcePath)
                .ReturnsAsync("job-1");
            _services.AddSingleton(queueMock.Object);

            var downloadItemServiceMock = new Mock<IDownloadItemService>();
            downloadItemServiceMock
                .Setup(r => r.ResolveImportItemAsync(It.IsAny<Download>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Download dl, CancellationToken __) =>
                {
                    return new QueueItem
                    {
                        ContentPath = sourceFile
                    };
                });
            _services.AddSingleton(downloadItemServiceMock.Object);
            Init();

            var settings = await _applicationSettingsRepository.SaveAsync(new ApplicationSettings
            {
                OutputPath = outputDir,
                CompletedFileAction = FileAction.Move,
                EnableMetadataProcessing = false,
                AllowedFileExtensions = [".m4b"]
            });

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfiguration
            {
                Name = clientType,
                Type = clientType,
                DownloadPath = sourceDir
            });

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithId($"dl-{clientType}")
                .WithStatus(DownloadStatus.Downloading)
                .WithDownloadClientConfiguration(client)
                .Build());

            var monitor = MockUtils.CreateDownloadMonitorService(_provider);

            var finalizeMethod = typeof(DownloadMonitorService).GetMethod("FinalizeDownloadAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(finalizeMethod);

            var finalizeTask = (Task?)finalizeMethod!.Invoke(monitor, [download, sourceDir, client, CancellationToken.None]);
            Assert.NotNull(finalizeTask);
            await finalizeTask;

            queueMock.Verify(q => q.QueueDownloadProcessingAsync(download.Id, It.IsAny<string>(), client.Id), Times.Once);
            Assert.Equal(Path.GetFullPath(sourceFile), Path.GetFullPath(queuedSource), ignoreCase: true);
        }
    }
}
