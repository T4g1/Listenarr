using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Listenarr.Api.Services.Metadata;

namespace Listenarr.Api.Tests
{
    public class ImportServiceTests
    {
        [Fact]
        public async Task ImportFilesFromDirectory_CreatesDestinationDirectory_WhenMissing()
        {
            // Arrange
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);

            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var file1 = Path.Join(sourceDir, "track1.m4b");
            var file2 = Path.Join(sourceDir, "track2.m4b");
            await File.WriteAllTextAsync(file1, "dummy");
            await File.WriteAllTextAsync(file2, "dummy");

            var settings = new ApplicationSettings { OutputPath = outputRoot, CompletedFileAction = "Move", EnableMetadataProcessing = false };

            // Build provider and register ImportService with an in-memory DB factory
            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options;

                var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
                dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                    .ReturnsAsync(new ListenArrDbContext(options));

                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<IFileNamingService>(new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()));
                services.AddSingleton<IMetadataService>(new Mock<IMetadataService>().Object);
                // ImportService uses NullFileMover by default when not provided, which is fine for tests
                services.AddSingleton<IImportService>(sp => new ImportService(dbFactoryMock.Object, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IFileNamingService>(), sp.GetService<IMetadataService>(), new NullLogger<ImportService>()));
            });

            var importService = provider.GetRequiredService<IImportService>();

            // Act
            var results = await importService.ImportFilesFromDirectoryAsync("dl-1", null, new[] { file1, file2 }, settings);

            // Assert: destination directory created
            Assert.True(Directory.Exists(outputRoot));

            // At least one successful import result should be present
            Assert.Contains(results, r => r.Success);

            // All successful results should point to files under the output root
            foreach (var r in results.Where(r => r.Success))
            {
                Assert.StartsWith(outputRoot.TrimEnd(Path.DirectorySeparatorChar), r.FinalPath, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(r.FinalPath));
            }

            // Cleanup
            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportSingleFile_WithAudiobookBasePath_DoesNotDuplicateFolderPatternSegments()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            var basePath = Path.Join(outputRoot, "Frank Herbert", "Dune");
            Directory.CreateDirectory(basePath);

            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Join(sourceDir, "dune-source.m4b");
            await File.WriteAllTextAsync(sourceFile, "dummy");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                FileNamingPattern = "{Author}/{Title}/{Title} ({Year})"
            };

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var seed = new ListenArrDbContext(options))
            {
                seed.Audiobooks.Add(new Audiobook
                {
                    Id = 123,
                    Title = "Dune",
                    Authors = new System.Collections.Generic.List<string> { "Frank Herbert" },
                    PublishYear = "2021",
                    BasePath = basePath
                });
                await seed.SaveChangesAsync();
            }

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<IFileNamingService>(new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()));
                services.AddSingleton<IMetadataService>(new Mock<IMetadataService>().Object);
                services.AddSingleton<IImportService>(sp => new ImportService(
                    dbFactoryMock.Object,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<IFileNamingService>(),
                    sp.GetService<IMetadataService>(),
                    new NullLogger<ImportService>()));
            });

            var importService = provider.GetRequiredService<IImportService>();

            var result = await importService.ImportSingleFileAsync("dl-1", 123, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar), result.FinalPath!, StringComparison.OrdinalIgnoreCase);

            var relative = Path.GetRelativePath(basePath, result.FinalPath!);
            Assert.Equal(Path.GetFileName(relative), relative);
            Assert.DoesNotContain($"Frank Herbert{Path.DirectorySeparatorChar}", relative, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"Dune{Path.DirectorySeparatorChar}", relative, StringComparison.OrdinalIgnoreCase);

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_WithLegacyFilePathAndNoBasePath_RegistersImportedFiles()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);

            var part2 = Path.Join(sourceDir, "Part 2.mp3");
            var part1 = Path.Join(sourceDir, "Part 1.mp3");
            await File.WriteAllTextAsync(part2, "two");
            await File.WriteAllTextAsync(part1, "one");

            var legacyDir = Path.Join(Path.GetTempPath(), $"import-legacy-{Guid.NewGuid()}");
            var legacyPath = Path.Join(legacyDir, "legacy.mp3");

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var seed = new ListenArrDbContext(options))
            {
                seed.Audiobooks.Add(new Audiobook
                {
                    Id = 456,
                    Title = "Jack of Shadows",
                    Authors = new System.Collections.Generic.List<string> { "Roger Zelazny" },
                    FilePath = legacyPath,
                    BasePath = null
                });
                await seed.SaveChangesAsync();
            }

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", Bitrate = 128000 });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddScoped(_ => new ListenArrDbContext(options));
                services.AddMemoryCache();
                services.AddSingleton<MetadataExtractionLimiter>();
                services.AddSingleton<IMetadataService>(metadataMock.Object);
            });

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()),
                metadataMock.Object,
                new NullLogger<ImportService>());

            var results = await importService.ImportFilesFromDirectoryAsync(
                "legacy-basepath",
                456,
                new[] { part2, part1 },
                settings);

            Assert.Equal(2, results.Count(r => r.Success));

            await using var verify = new ListenArrDbContext(options);
            var audiobook = await verify.Audiobooks.FindAsync(456);
            var files = await verify.AudiobookFiles.Where(f => f.AudiobookId == 456).ToListAsync();

            Assert.NotNull(audiobook);
            Assert.Equal(Path.GetFullPath(outputRoot), audiobook!.BasePath);
            Assert.Equal(2, files.Count);
            Assert.Contains(files, f => f.Path == Path.Join(outputRoot, "Jack of Shadows-01.mp3"));
            Assert.Contains(files, f => f.Path == Path.Join(outputRoot, "Jack of Shadows-02.mp3"));

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_ForewordAndChapterOne_GetStableUniqueSequenceNames()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);

            var foreword = Path.Join(sourceDir, "(Foreword by Joe Haldeman).mp3");
            var chapter1 = Path.Join(sourceDir, "Chapter 01.mp3");
            var chapter2 = Path.Join(sourceDir, "Chapter 02.mp3");
            await File.WriteAllTextAsync(foreword, "foreword");
            await File.WriteAllTextAsync(chapter1, "chapter1");
            await File.WriteAllTextAsync(chapter2, "chapter2");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("(Foreword by Joe Haldeman).mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Chapter 01.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.Is<string>(path => path.EndsWith("Chapter 02.mp3", StringComparison.OrdinalIgnoreCase))))
                .ReturnsAsync(new AudioMetadata { Title = "Jack of Shadows", Format = "mp3", TrackNumber = 2 });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            using var provider = TestServiceFactory.BuildServiceProvider(_ => { });

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()),
                metadataMock.Object,
                new NullLogger<ImportService>());

            var results = await importService.ImportFilesFromDirectoryAsync(
                "foreword-order",
                audiobookId: null,
                new[] { foreword, chapter1, chapter2 },
                settings);

            var mapped = results
                .Where(r => r.Success && !string.IsNullOrWhiteSpace(r.SourcePath) && !string.IsNullOrWhiteSpace(r.FinalPath))
                .ToDictionary(r => r.SourcePath!, r => r.FinalPath!, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(Path.Join(outputRoot, "Jack of Shadows-01.mp3"), mapped[foreword]);
            Assert.Equal(Path.Join(outputRoot, "Jack of Shadows-02.mp3"), mapped[chapter1]);
            Assert.Equal(Path.Join(outputRoot, "Jack of Shadows-03.mp3"), mapped[chapter2]);

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_MoveImportsCompanionFilesAndDeletesSourceFolder()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);

            var audioFile = Path.Join(sourceDir, "Track 01.mp3");
            var coverFile = Path.Join(sourceDir, "cover.jpg");
            var notesFile = Path.Join(sourceDir, "notes.txt");
            await File.WriteAllTextAsync(audioFile, "audio");
            await File.WriteAllTextAsync(coverFile, "cover");
            await File.WriteAllTextAsync(notesFile, "notes");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}",
                ImportBlacklistExtensions = new System.Collections.Generic.List<string>()
            };

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(audioFile))
                .ReturnsAsync(new AudioMetadata { Title = "Companion Book", Format = "mp3", Bitrate = 128000 });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            using var provider = TestServiceFactory.BuildServiceProvider(_ => { });

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()),
                metadataMock.Object,
                new NullLogger<ImportService>());

            var results = await importService.ImportFilesFromDirectoryAsync(
                "companion-download",
                audiobookId: null,
                new[] { audioFile, coverFile, notesFile },
                settings);

            Assert.Equal(3, results.Count(r => r.Success));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "Companion Book-01.mp3", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "cover.jpg", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "notes.txt", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(sourceDir));

            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_BlacklistedCompanionFilesAreSkipped()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);

            var audioFile = Path.Join(sourceDir, "Track 01.mp3");
            var coverFile = Path.Join(sourceDir, "cover.jpg");
            var notesFile = Path.Join(sourceDir, "notes.txt");
            await File.WriteAllTextAsync(audioFile, "audio");
            await File.WriteAllTextAsync(coverFile, "cover");
            await File.WriteAllTextAsync(notesFile, "notes");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}",
                ImportBlacklistExtensions = new System.Collections.Generic.List<string> { ".txt" }
            };

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(audioFile))
                .ReturnsAsync(new AudioMetadata { Title = "Companion Book", Format = "mp3", Bitrate = 128000 });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            using var provider = TestServiceFactory.BuildServiceProvider(_ => { });

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()),
                metadataMock.Object,
                new NullLogger<ImportService>());

            var results = await importService.ImportFilesFromDirectoryAsync(
                "companion-download-blacklist",
                audiobookId: null,
                new[] { audioFile, coverFile, notesFile },
                settings);

            Assert.Equal(2, results.Count(r => r.Success));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "Companion Book-01.mp3", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(results, r => string.Equals(Path.GetFileName(r.FinalPath), "cover.jpg", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(results, r => string.Equals(Path.GetFileName(r.FinalPath), "notes.txt", StringComparison.OrdinalIgnoreCase));
            Assert.True(Directory.Exists(sourceDir));
            Assert.True(File.Exists(notesFile));

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_NestedTorrentFolders_DoNotDuplicateSeriesAndTitleSegments()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            var sourceRoot = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            var title = "Murder by Other Means: The Dispatcher/Book 2";
            var author = "John Scalzi";
            var series = "The Dispatcher";
            var sanitizedTitle = SanitizePathComponentForCurrentPlatform(title);

            var nestedSourceDir = Path.Join(sourceRoot, series, sanitizedTitle);
            Directory.CreateDirectory(nestedSourceDir);

            var sourceFile = Path.Join(nestedSourceDir, $"{sanitizedTitle}.m4b");
            await File.WriteAllTextAsync(sourceFile, "dummy");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Author}/{Series}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(sourceFile))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = title,
                    Artist = author,
                    AlbumArtist = author,
                    Series = series,
                    Format = "m4b"
                });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            using var provider = TestServiceFactory.BuildServiceProvider(_ => { });

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()),
                metadataMock.Object,
                new NullLogger<ImportService>());

            var results = await importService.ImportFilesFromDirectoryAsync(
                "nested-torrent",
                audiobookId: null,
                new[] { sourceFile },
                settings);

            var success = Assert.Single(results, r => r.Success);
            Assert.NotNull(success.FinalPath);

            var actualFullPath = Path.GetFullPath(success.FinalPath!);
            var actualRelativePath = Path.GetRelativePath(outputRoot, actualFullPath);
            var actualDirectory = Path.GetDirectoryName(actualRelativePath);
            var actualFileName = Path.GetFileName(actualRelativePath);
            var pathSeparator = Path.DirectorySeparatorChar.ToString();

            Assert.Equal(string.Join(pathSeparator, author, series, sanitizedTitle), actualDirectory);
            Assert.StartsWith(sanitizedTitle, actualFileName, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".m4b", actualFileName, StringComparison.OrdinalIgnoreCase);

            var duplicatedSegment = string.Join(pathSeparator, series, sanitizedTitle, series, sanitizedTitle);
            Assert.DoesNotContain(duplicatedSegment, actualFullPath, StringComparison.OrdinalIgnoreCase);

            TryDeleteDirectory(sourceRoot, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportSingleFile_WithWindowsShortBasePath_NormalizesFinalPath()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            var longBasePath = Path.Join(outputRoot, "A Very Long Audiobook Folder Name");
            Directory.CreateDirectory(longBasePath);

            var shortBasePath = TryGetShortPathName(longBasePath);
            if (string.IsNullOrWhiteSpace(shortBasePath)
                || string.Equals(shortBasePath, longBasePath, StringComparison.OrdinalIgnoreCase)
                || !shortBasePath.Contains('~'))
            {
                TryDeleteDirectory(outputRoot, recursive: true);
                return;
            }

            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Join(sourceDir, "source-track.m4b");
            await File.WriteAllTextAsync(sourceFile, "dummy");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Move",
                EnableMetadataProcessing = false,
                FileNamingPattern = "{Title}"
            };

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var seed = new ListenArrDbContext(options))
            {
                seed.Audiobooks.Add(new Audiobook
                {
                    Id = 321,
                    Title = "A Great Book",
                    Authors = new System.Collections.Generic.List<string> { "Test Author" },
                    BasePath = shortBasePath
                });
                await seed.SaveChangesAsync();
            }

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddSingleton<IDbContextFactory<ListenArrDbContext>>(dbFactoryMock.Object);
                services.AddSingleton<IFileNamingService>(new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()));
                services.AddSingleton<IMetadataService>(new Mock<IMetadataService>().Object);
                services.AddSingleton<IImportService>(sp => new ImportService(
                    dbFactoryMock.Object,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<IFileNamingService>(),
                    sp.GetService<IMetadataService>(),
                    new NullLogger<ImportService>()));
            });

            var importService = provider.GetRequiredService<IImportService>();

            var result = await importService.ImportSingleFileAsync("dl-short-path", 321, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.StartsWith(longBasePath.TrimEnd(Path.DirectorySeparatorChar), result.FinalPath!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("~", result.FinalPath!, StringComparison.Ordinal);

            await using var verify = new ListenArrDbContext(options);
            var stored = await verify.Audiobooks.FindAsync(321);
            Assert.NotNull(stored);
            Assert.Equal(longBasePath, stored!.BasePath);

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportSingleFile_WithAudiobookNarrators_AllowsNarratorTokenInNamingPattern()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            Directory.CreateDirectory(outputRoot);

            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Join(sourceDir, "gunslinger-source.m4b");
            await File.WriteAllTextAsync(sourceFile, "dummy");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title} ({Narrator})",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var seed = new ListenArrDbContext(options))
            {
                seed.Audiobooks.Add(new Audiobook
                {
                    Id = 987,
                    Title = "The Gunslinger",
                    Authors = new System.Collections.Generic.List<string> { "Stephen King" },
                    Narrators = new System.Collections.Generic.List<string> { "George Guidall", "Frank Muller" }
                });
                await seed.SaveChangesAsync();
            }

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "The Gunslinger", Format = "m4b" });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddScoped(_ => new ListenArrDbContext(options));
                services.AddMemoryCache();
                services.AddSingleton<MetadataExtractionLimiter>();
                services.AddSingleton<IMetadataService>(metadataMock.Object);
            });

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()),
                metadataMock.Object,
                new NullLogger<ImportService>());

            var result = await importService.ImportSingleFileAsync("dl-narrator", 987, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.Contains("George Guidall, Frank Muller", result.FinalPath!, StringComparison.Ordinal);
            Assert.True(File.Exists(result.FinalPath));

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportSingleFile_WithoutAuthors_DoesNotUseNarratorAsAuthorFallback()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            Directory.CreateDirectory(outputRoot);

            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Join(sourceDir, "gunslinger-source.m4b");
            await File.WriteAllTextAsync(sourceFile, "dummy");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Author}/{Title}",
                FileNamingPattern = "{Title}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var seed = new ListenArrDbContext(options))
            {
                seed.Audiobooks.Add(new Audiobook
                {
                    Id = 988,
                    Title = "The Gunslinger",
                    Narrators = new System.Collections.Generic.List<string> { "George Guidall" }
                });
                await seed.SaveChangesAsync();
            }

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata
                {
                    Title = "The Gunslinger",
                    Format = "m4b",
                    AlbumArtist = "George Guidall",
                    Narrator = "George Guidall"
                });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddScoped(_ => new ListenArrDbContext(options));
                services.AddMemoryCache();
                services.AddSingleton<MetadataExtractionLimiter>();
                services.AddSingleton<IMetadataService>(metadataMock.Object);
            });

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()),
                metadataMock.Object,
                new NullLogger<ImportService>());

            var result = await importService.ImportSingleFileAsync("dl-author-fallback", 988, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.Contains($"Unknown Author{Path.DirectorySeparatorChar}The Gunslinger", result.FinalPath!, StringComparison.Ordinal);
            Assert.DoesNotContain($"George Guidall{Path.DirectorySeparatorChar}The Gunslinger", result.FinalPath!, StringComparison.Ordinal);

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportSingleFile_WithAudiobookMetadata_SupportsSubtitlePublisherLanguageAndAsinTokens()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            Directory.CreateDirectory(outputRoot);

            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var sourceFile = Path.Join(sourceDir, "gunslinger-source.m4b");
            await File.WriteAllTextAsync(sourceFile, "dummy");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Publisher}/{Language}/{Asin}",
                FileNamingPattern = "{Title} - {Edition} - {Subtitle}",
                MultiFileNamingPattern = "{Title}-{DiskNumber:00}"
            };

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var seed = new ListenArrDbContext(options))
            {
                seed.Audiobooks.Add(new Audiobook
                {
                    Id = 989,
                    Title = "The Gunslinger",
                    Subtitle = "The Dark Tower Begins",
                    Authors = new System.Collections.Generic.List<string> { "Stephen King" },
                    Publisher = "Penguin Audio",
                    Language = "English",
                    Asin = "B000FC1R84",
                    Edition = "Revised Edition"
                });
                await seed.SaveChangesAsync();
            }

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Title = "The Gunslinger", Format = "m4b" });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddScoped(_ => new ListenArrDbContext(options));
                services.AddMemoryCache();
                services.AddSingleton<MetadataExtractionLimiter>();
                services.AddSingleton<IMetadataService>(metadataMock.Object);
            });

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()),
                metadataMock.Object,
                new NullLogger<ImportService>());

            var result = await importService.ImportSingleFileAsync("dl-metadata-vars", 989, sourceFile, settings);

            Assert.True(result.Success);
            Assert.NotNull(result.FinalPath);
            Assert.Contains($"Penguin Audio{Path.DirectorySeparatorChar}English{Path.DirectorySeparatorChar}B000FC1R84", result.FinalPath!, StringComparison.Ordinal);
            Assert.Contains("The Gunslinger - Revised Edition - The Dark Tower Begins.m4b", result.FinalPath!, StringComparison.Ordinal);

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        [Fact]
        public async Task ImportFilesFromDirectory_WithAudiobookMetadata_SupportsEditionSubtitlePublisherLanguageAndAsinTokens()
        {
            var outputRoot = Path.Join(Path.GetTempPath(), $"import-out-{Guid.NewGuid()}");
            Directory.CreateDirectory(outputRoot);

            var sourceDir = Path.Join(Path.GetTempPath(), $"import-src-{Guid.NewGuid()}");
            Directory.CreateDirectory(sourceDir);
            var firstSourceFile = Path.Join(sourceDir, "gunslinger-source-1.m4b");
            var secondSourceFile = Path.Join(sourceDir, "gunslinger-source-2.m4b");
            await File.WriteAllTextAsync(firstSourceFile, "dummy-1");
            await File.WriteAllTextAsync(secondSourceFile, "dummy-2");

            var settings = new ApplicationSettings
            {
                OutputPath = outputRoot,
                CompletedFileAction = "Copy",
                EnableMetadataProcessing = false,
                FolderNamingPattern = "{Publisher}/{Language}/{Asin}",
                FileNamingPattern = "{Title} - {Edition} - {Subtitle}",
                MultiFileNamingPattern = "{Title} - {Edition} - {Subtitle} - {DiskNumber:00}"
            };

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            await using (var seed = new ListenArrDbContext(options))
            {
                seed.Audiobooks.Add(new Audiobook
                {
                    Id = 990,
                    Title = "The Gunslinger",
                    Subtitle = "The Dark Tower Begins",
                    Authors = new System.Collections.Generic.List<string> { "Stephen King" },
                    Publisher = "Penguin Audio",
                    Language = "English",
                    Asin = "B000FC1R84",
                    Edition = "Revised Edition"
                });
                await seed.SaveChangesAsync();
            }

            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(firstSourceFile))
                .ReturnsAsync(new AudioMetadata { Title = "The Gunslinger", Format = "m4b", DiscNumber = 1 });
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(secondSourceFile))
                .ReturnsAsync(new AudioMetadata { Title = "The Gunslinger", Format = "m4b", DiscNumber = 2 });

            var dbFactoryMock = new Mock<IDbContextFactory<ListenArrDbContext>>();
            dbFactoryMock
                .Setup(f => f.CreateDbContextAsync(It.IsAny<System.Threading.CancellationToken>()))
                .ReturnsAsync(() => new ListenArrDbContext(options));

            using var provider = TestServiceFactory.BuildServiceProvider(services =>
            {
                services.AddScoped(_ => new ListenArrDbContext(options));
                services.AddMemoryCache();
                services.AddSingleton<MetadataExtractionLimiter>();
                services.AddSingleton<IMetadataService>(metadataMock.Object);
            });

            var importService = new ImportService(
                dbFactoryMock.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new FileNamingService(new TestConfigurationService(), new NullLogger<FileNamingService>()),
                metadataMock.Object,
                new NullLogger<ImportService>());

            var results = await importService.ImportFilesFromDirectoryAsync(
                "dl-dir-metadata-vars",
                990,
                new[] { firstSourceFile, secondSourceFile },
                settings);

            var successfulResults = results.Where(item => item.Success).ToList();
            Assert.Equal(2, successfulResults.Count);
            Assert.All(successfulResults, result =>
            {
                Assert.NotNull(result.FinalPath);
                Assert.Contains($"Penguin Audio{Path.DirectorySeparatorChar}English{Path.DirectorySeparatorChar}B000FC1R84", result.FinalPath!, StringComparison.Ordinal);
                Assert.Contains("The Gunslinger - Revised Edition - The Dark Tower Begins", result.FinalPath!, StringComparison.Ordinal);
            });

            TryDeleteDirectory(sourceDir, recursive: true);
            TryDeleteDirectory(outputRoot, recursive: true);
        }

        private static void TryDeleteDirectory(string path, bool recursive = false)
        {
            try
            {
                Directory.Delete(path, recursive);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.Error.WriteLine($"Ignoring cleanup failure for '{path}': {ex.Message}");
            }
        }

        private static string SanitizePathComponentForCurrentPlatform(string value)
        {
            var sanitized = new StringBuilder();

            foreach (var c in value)
            {
                if (char.IsControl(c))
                {
                    continue;
                }

                if (c == ':' || c == '/' || c == '\\')
                {
                    sanitized.Append(" - ");
                }
                else if (Path.GetInvalidFileNameChars().Contains(c) || "<>:\"/\\|?*".Contains(c))
                {
                    sanitized.Append('_');
                }
                else
                {
                    sanitized.Append(c);
                }
            }

            var result = sanitized.ToString();
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"(?:\s*-\s*){2,}", " - ");
            result = System.Text.RegularExpressions.Regex.Replace(result, @"_+", "_");
            result = result.Trim().TrimEnd('.', ' ');
            result = System.Text.RegularExpressions.Regex.Replace(result, @"^\s*[-_]+\s*", string.Empty);
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\s*[-_]+\s*$", string.Empty);

            if (string.Equals(result, "CON", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result, "PRN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result, "AUX", StringComparison.OrdinalIgnoreCase)
                || string.Equals(result, "NUL", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(result, @"^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                result += "_";
            }

            return string.IsNullOrWhiteSpace(result) ? "Unknown" : result;
        }

        private static string? TryGetShortPathName(string longPath)
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(longPath))
            {
                return null;
            }

            var buffer = new StringBuilder(260);
            var result = GetShortPathName(longPath, buffer, buffer.Capacity);
            if (result == 0)
            {
                return null;
            }

            if (result > buffer.Capacity)
            {
                buffer = new StringBuilder((int)result);
                result = GetShortPathName(longPath, buffer, buffer.Capacity);
                if (result == 0)
                {
                    return null;
                }
            }

            return buffer.ToString();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPathBuffer, int bufferLength);
    }
}
