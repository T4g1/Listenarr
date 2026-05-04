using Listenarr.Application.Models;
using Listenarr.Application.Models.Configurations;
using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Download import responsible for processing a given download importation
    /// </summary>
    public interface IDownloadImportService
    {
        /// <summary>
        /// Import a single completed file
        /// </summary>
        Task<ImportResult> ImportSingleFileAsync(Download download, Audiobook audiobook, string sourcePath, ApplicationSettings settings, CancellationToken ct = default);

        /// <summary>
        /// Import multiple files from a directory (multi-file import)
        /// </summary>
        Task<List<ImportResult>> ImportFilesFromDirectoryAsync(Download download, Audiobook audiobook, IEnumerable<string> files, ApplicationSettings settings, CancellationToken ct = default);

        /// <summary>
        /// Reprocess an existing file path (useful for reprocessing completed downloads)
        /// </summary>
        Task<ImportResult> ReprocessExistingFileAsync(Download download, Audiobook audiobook, string sourcePath, ApplicationSettings settings, CancellationToken ct = default);
    }
}
