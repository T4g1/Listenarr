namespace Listenarr.Api.Services.Metadata
{
    /// <summary>
    /// Provides metadata retrieval and file tagging for audiobook files
    /// </summary>
    public interface IMetadataService
    {
        /// <summary>
        /// Retrieve metadata for the file related to the given job
        /// </summary>
        /// <param name="job">Job to process metadata for</param>
        /// <param name="download">Related download if any</param>
        /// <param name="audiobook">Related audiobook if any</param>
        /// <param name="cancellationToken"></param>
        /// <returns>Compiled metadata from all known sources</returns>
        Task<AudioMetadata> FetchMetadataAsync(
            DownloadProcessingJob job,
            Download? download,
            Audiobook? audiobook,
            CancellationToken cancellationToken);
        
        /// <summary>
        /// Gets metadata from online sources
        /// </summary>
        /// <param name="title">The audiobook title</param>
        /// <param name="artist">Optional artist/author name</param>
        /// <param name="isbn">Optional ISBN</param>
        /// <returns>Audio metadata or null if not found</returns>
        Task<AudioMetadata?> GetMetadataAsync(string title, string? artist = null, string? isbn = null);

        /// <summary>
        /// Extracts metadata from an audio file using ffprobe
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <returns>Extracted audio metadata, or null if extraction failed</returns>
        Task<AudioMetadata?> ExtractFileMetadataAsync(string filePath);

        /// <summary>
        /// Applies metadata tags to an audio file
        /// </summary>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="metadata">Metadata to apply</param>
        Task ApplyMetadataAsync(string filePath, AudioMetadata metadata);

        /// <summary>
        /// Writes the ASIN to the audio file's embedded tags. No-op if filePath or asin is null/empty.
        /// </summary>
        Task WriteAsinTagAsync(string filePath, string asin);

        /// <summary>
        /// Downloads cover art image from URL
        /// </summary>
        /// <param name="coverArtUrl">URL of the cover art image</param>
        /// <returns>Image data as byte array or null if failed</returns>
        Task<byte[]?> DownloadCoverArtAsync(string coverArtUrl);
    }
}