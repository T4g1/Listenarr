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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Listenarr.Api.Controllers;
using Listenarr.Domain.Models;
using Listenarr.Api.Services;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_AddToLibraryTests : BaseTests
    {
        private readonly Mock<IImageCacheService> imageCacheServiceMock = new Mock<IImageCacheService>();
        private readonly string imageUrl1 = "http://example.com/a1.jpg";
        private readonly string imageUrl2 = "http://example.com/a2.jpg";
        public override async Task InitializeAsync()
        {
            imageCacheServiceMock.Setup(m => m.MoveToLibraryStorageAsync(It.IsAny<string>(), imageUrl1)).ReturnsAsync("config/cache/images/library/B000TEST01.jpg");
            imageCacheServiceMock.Setup(m => m.MoveToLibraryStorageAsync(It.IsAny<string>(), imageUrl2)).ReturnsAsync("config/cache/images/library/derived.jpg");
            _services.AddSingleton(imageCacheServiceMock.Object);

            Init();
            await InitDataAsync();
        }

        private async Task InitDataAsync()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFileNamingPattern("{Author}/{Title}")
                .Build());
        }

        private static string BuildLibraryPath(Dictionary<string, object> vars)
        {
            vars.TryGetValue("Author", out var authorObj);
            vars.TryGetValue("Title", out var titleObj);

            var author = authorObj?.ToString() ?? "Unknown";
            var title = titleObj?.ToString() ?? "Unknown";

            return Path.Join(author, title).Replace("\\", "/");
        }

        [Fact]
        public async Task AddToLibrary_UsesLegacyAuthorField_PopulatesAuthorsAndBasePath()
        {
            // Configuration service providing an OutputPath root
            var tempRoot = FileService.GetTempDirectory("listenarr-test");

            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Legacy Title",
                    Author = "Legacy Author"
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            Assert.NotNull(stored.Authors);
            Assert.Contains("Legacy Author", stored.Authors);
            // BasePath should only be set when a custom destination path is explicitly provided
            // When no custom path is given, ImportService uses the default file naming pattern from settings
            Assert.True(string.IsNullOrWhiteSpace(stored.BasePath), "BasePath should be null when no custom destination is provided");
        }

        [Fact]
        public async Task AddToLibrary_PersistsEditableMetadataFields()
        {
            // Arrange
            var tempRoot = FileService.GetTempDirectory("listenarr-test");

            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Editable Title",
                    Subtitle = "Editable Subtitle",
                    Authors = new List<string> { "Edited Author" },
                    Narrators = new List<string> { "Edited Narrator" },
                    Publisher = "Edited Publisher",
                    Language = "english",
                    Runtime = 615,
                    Edition = "Collector's Edition",
                    Version = "Audible Version",
                    Asin = "B00EDIT123",
                    Isbn = new List<string> { "9781234567890" },
                    OpenLibraryId = "OL12345M"
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            Assert.Equal("Editable Title", stored.Title);
            Assert.Equal("Editable Subtitle", stored.Subtitle);
            Assert.Equal("Edited Publisher", stored.Publisher);
            Assert.Equal("english", stored.Language);
            Assert.Equal(615, stored.Runtime);
            Assert.Equal("Collector's Edition", stored.Edition);
            Assert.Equal("Audible Version", stored.Version);
            Assert.Equal("B00EDIT123", stored.Asin);
            Assert.Equal("OL12345M", stored.OpenLibraryId);
            Assert.NotNull(stored.Authors);
            Assert.Contains("Edited Author", stored.Authors);
            Assert.NotNull(stored.Narrators);
            Assert.Contains("Edited Narrator", stored.Narrators);
            Assert.NotNull(stored.Isbn);
            Assert.Contains("9781234567890", stored.Isbn);
        }

        [Fact]
        public async Task AddToLibrary_WithAsin_MovesImageToLibraryStorage()
        {
            var asin = "B000TEST01";

            // Configuration service providing an OutputPath root
            var tempRoot = FileService.GetTempDirectory("listenarr-test");

            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Move Test",
                    Author = "A Uthor",
                    Asin = "B000TEST01",
                    ImageUrl = imageUrl1
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            Assert.Equal($"/config/cache/images/library/B000TEST01.jpg", stored.ImageUrl);
            imageCacheServiceMock.Verify(m => m.MoveToLibraryStorageAsync(asin, imageUrl1), Times.Once);
        }

        [Fact]
        public async Task AddToLibrary_WithoutAsin_UsesDerivedKey_AndMovesImageToLibraryStorage()
        {
            // Configuration service providing an OutputPath root
            var tempRoot = FileService.GetTempDirectory("listenarr-test");

            var controller = _provider.GetRequiredService<LibraryController>();

            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Derived Test",
                    Author = "Some Author",
                    ImageUrl = imageUrl2
                },
                Monitored = true
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            Assert.Equal($"/config/cache/images/library/derived.jpg", stored.ImageUrl);
            imageCacheServiceMock.Verify(m => m.MoveToLibraryStorageAsync(It.IsAny<string>(), imageUrl2), Times.Once);
        }

        [Fact]
        public async Task AddToLibrary_WithCustomPath_StoresCustomPathAsBasePath()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var customPath = "/custom/audiobooks/Author/Series/Title";
            var request = new LibraryController.AddToLibraryRequest
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Custom Path Test",
                    Author = "Custom Author"
                },
                Monitored = true,
                DestinationPath = customPath  // Custom path provided
            };

            // Act
            var actionResult = await controller.AddToLibrary(request);

            // Assert
            Assert.IsType<OkObjectResult>(actionResult);

            var stored = (await _audiobookRepository.GetAllAsync()).First();
            Assert.NotNull(stored);
            // NormalizeStoredPath calls Path.GetFullPath which is platform-dependent:
            // on Windows "/custom/..." becomes "C:\custom\...", on Linux it stays "/custom/..."
            var expectedPath = Path.GetFullPath(customPath);
            Assert.Equal(expectedPath, stored.BasePath);
        }
    }
}
