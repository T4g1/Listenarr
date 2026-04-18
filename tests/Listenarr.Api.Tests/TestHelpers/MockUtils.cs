using System.Net;
using System.Text;
using Listenarr.Api.Hubs;
using Listenarr.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestPlatform.Utilities;
using Moq;

namespace Listenarr.Api.Tests
{
    public abstract class MockUtils
    {
        /// <summary>
        /// Returns a 200 HTTP reply with the given JSON as content
        /// </summary>
        /// <param name="json">JSON structure to use as a response body</param>
        /// <returns>HttpResponseMessage with status 200 and JSON body</returns>
        public static HttpResponseMessage GetCannedResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        public static Mock<DownloadMonitorService> GetDownloadMonitorServiceMock()
        {
            var scopeFactoryMock = new Mock<IServiceScopeFactory>();
            var scopeMock = new Mock<IServiceScope>();
            var serviceProviderMock = new Mock<IServiceProvider>();

            scopeFactoryMock.Setup(x => x.CreateScope()).Returns(scopeMock.Object);
            scopeMock.Setup(x => x.ServiceProvider).Returns(serviceProviderMock.Object);

            return new Mock<DownloadMonitorService>(
                scopeFactoryMock.Object,
                new Mock<IHubContext<DownloadHub>>().Object,
                new Mock<ILogger<DownloadMonitorService>>().Object,
                new Mock<IHttpClientFactory>().Object,
                new Mock<IAppMetricsService>().Object);
        }

        public static ServiceProvider CreateServiceProvider(IImportItemResolutionService importItemResolutionService, ListenArrDbContext db, string outputPath)
        {
            var metricsMock = new Mock<IAppMetricsService>();

            var configMock = new Mock<IConfigurationService>();
            configMock.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings
            {
                OutputPath = outputPath,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false
            });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(db);
            services.AddSingleton(configMock.Object);
            services.AddSingleton(importItemResolutionService);
            services.AddSingleton(metricsMock.Object);
            services.AddSingleton(new Mock<IDownloadService>().Object);
            services.AddMemoryCache(); 
            services.AddScoped<IRemotePathMappingService, RemotePathMappingService>();
            services.AddScoped<IDownloadProcessingQueueService, DownloadProcessingQueueService>();

            return services.BuildServiceProvider();
        }

        public static async Task<DownloadProcessingJob> CreateDownloadProcessingJob(ServiceProvider provider, Download download, string sourcePath)
        {
            var queueService = provider.GetRequiredService<IDownloadProcessingQueueService>();
            var jobId = await queueService.QueueDownloadProcessingAsync(download.Id, sourcePath, null);
            var job = await queueService.GetJobAsync(jobId);

            // Set job to processing (the outer loop normally does this)
            job.Status = ProcessingJobStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
            await queueService.UpdateJobAsync(job);

            return job;
        }
    }
}
