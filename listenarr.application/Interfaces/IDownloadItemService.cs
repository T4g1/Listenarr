using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Resolves download informations with accurate paths and metadata as queue items
    /// </summary>
    public interface IDownloadItemService
    {
        /// <summary>
        /// Resolves the import item by querying the download client.
        /// Called just before import to get the most accurate path.
        /// </summary>
        Task<QueueItem> ResolveImportItemAsync(Download download, CancellationToken cancellationToken = default);

        /// <summary>
        /// List files in the given folder and check which ones belongs to the given download
        /// </summary>
        /// <param name="download">Download to which we want to match files</param>
        /// <param name="localPath">Local Listenarr path where files are located</param>
        /// <param name="cancellationToken"></param>
        /// <returns>List of files that are in the given local directory and also part of the given download</returns>
        /// <exception cref="DownloadProcessingException">Thrown when we are technicaly unable to perform the filtering based on download client retrieved informations</exception>
        Task<List<string>> MatchLocalAndDownloadedFilesAsync(Download download, string localPath, CancellationToken cancellationToken = default);
    }
}
