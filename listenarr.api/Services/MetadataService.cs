/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
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

using Listenarr.Domain.Models;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;

namespace Listenarr.Api.Services
{
    public class MetadataService : IMetadataService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfigurationService _configurationService;
        private readonly IFfmpegService _ffmpegService;
        private readonly ILogger<MetadataService> _logger;
        private readonly IProcessRunner? _processRunner;

        public MetadataService(HttpClient httpClient, IConfigurationService configurationService, ILogger<MetadataService> logger, IFfmpegService ffmpegService, IProcessRunner? processRunner = null)
        {
            _httpClient = httpClient;
            _configurationService = configurationService;
            _ffmpegService = ffmpegService;
            _logger = logger;
            _processRunner = processRunner;
        }

        public async Task<AudioMetadata?> GetMetadataAsync(string title, string? artist = null, string? isbn = null)
        {
            try
            {
                var settings = await _configurationService.GetApplicationSettingsAsync();
                var audnexusUrl = settings.AudnexusApiUrl;

                // Build search query for Audnexus API
                string searchQuery;
                if (!string.IsNullOrEmpty(isbn))
                {
                    searchQuery = $"{audnexusUrl}/books/{isbn}";
                }
                else
                {
                    var queryParams = new List<string>();
                    if (!string.IsNullOrEmpty(title)) queryParams.Add($"title={Uri.EscapeDataString(title)}");
                    if (!string.IsNullOrEmpty(artist)) queryParams.Add($"author={Uri.EscapeDataString(artist)}");

                    searchQuery = $"{audnexusUrl}/search?" + string.Join("&", queryParams);
                }

                _logger.LogInformation("Fetching metadata from Audnexus: {Query}", LogRedaction.SanitizeUrl(searchQuery));

                var response = await _httpClient.GetAsync(searchQuery);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Audnexus API returned {Status} for query: {Query}", response.StatusCode, LogRedaction.SanitizeUrl(searchQuery));
                    return null;
                }

                var jsonContent = await response.Content.ReadAsStringAsync();

                // Parse Audnexus response and convert to AudioMetadata
                // This is a simplified implementation - you would need to adapt based on actual Audnexus API structure
                var audnexusData = JsonSerializer.Deserialize<JsonElement>(jsonContent);

                return ParseAudnexusResponse(audnexusData);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error fetching metadata for title: {Title}, artist: {Artist}", LogRedaction.SanitizeText(title), LogRedaction.SanitizeText(artist));
                return null;
            }
        }

        public async Task<AudioMetadata?> ExtractFileMetadataAsync(string filePath)
        {
            try
            {
                // If the file doesn't exist, skip running ffprobe and return basic metadata from filename
                if (!File.Exists(filePath))
                {
                    _logger.LogWarning("File not found when attempting metadata extraction: {File}", LogRedaction.SanitizeFilePath(filePath));
                    var fallbackMissingFile = new AudioMetadata
                    {
                        Title = Path.GetFileNameWithoutExtension(filePath),
                        Format = Path.GetExtension(filePath).TrimStart('.').ToUpper()
                    };
                    _logger.LogInformation("Extracted basic metadata from (missing) file: {File}", LogRedaction.SanitizeText(filePath));
                    return fallbackMissingFile;
                }

                // Ask the ffmpeg installer/service for the bundled ffprobe path
                var ffprobePathService = await _ffmpegService.GetFfprobePathAsync();
                if (string.IsNullOrEmpty(ffprobePathService) || !File.Exists(ffprobePathService))
                {
                    _logger.LogInformation("No bundled ffprobe available at configured location; skipping ffprobe for file: {File}", LogRedaction.SanitizeFilePath(filePath));
                    // Let the outer method fall back to filename-based metadata
                    return null;
                }
            
                try
                {
                    var ffprobeResult = await _ffmpegService.RunFfprobeAsync(filePath);
                    if (ffprobeResult != null)
                    {
                        return ffprobeResult;
                    }
                }
                catch(FfmpegException ex)
                {
                    _logger.LogWarning(ex, "Unable to extract metadata using ffprobe: Using basic filename based metadatas");
                }

                // Fallback: basic filename-based metadata
                var fallbackName = Path.GetFileNameWithoutExtension(filePath);
                var fallback = new AudioMetadata
                {
                    Title = fallbackName,
                    Format = Path.GetExtension(filePath).TrimStart('.').ToUpper()
                };

                _logger.LogInformation("Extracted basic metadata from file: {File}", LogRedaction.SanitizeFilePath(filePath));
                return fallback;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error extracting metadata from file: {File}", LogRedaction.SanitizeFilePath(filePath));
                return new AudioMetadata();
            }
        }

        public async Task ApplyMetadataAsync(string filePath, AudioMetadata metadata)
        {
            try
            {
                // This would use a library like TagLib# to apply metadata to audio files
                _logger.LogInformation("Applied metadata to file: {File}", LogRedaction.SanitizeText(filePath));
                await Task.CompletedTask;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error applying metadata to file: {File}", LogRedaction.SanitizeText(filePath));
            }
        }

        public Task WriteAsinTagAsync(string filePath, string asin)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(asin))
                return Task.CompletedTask;
            try
            {
                using var file = TagLib.File.Create(filePath);

                // M4B / M4A / MP4 — iTunes freeform dash box  ----:com.apple.iTunes:ASIN
                if (file.Tag is TagLib.Mpeg4.AppleTag appleTag)
                    appleTag.SetDashBox("com.apple.iTunes", "ASIN", asin);
                // MP3 — TXXX frame with description "ASIN"
                else if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3Tag)
                {
                    var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, "ASIN", true);
                    frame.Text = new[] { asin };
                }
                // FLAC / OGG / Opus — Vorbis comment
                else if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                    xiph.SetField("ASIN", asin);
                else
                    return Task.CompletedTask; // Unknown format — skip silently

                file.Save();
                _logger.LogDebug("Wrote ASIN tag '{Asin}' to {File}", asin, LogRedaction.SanitizeFilePath(filePath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to write ASIN tag to {File} — import will continue", LogRedaction.SanitizeFilePath(filePath));
            }
            return Task.CompletedTask;
        }

        public async Task<byte[]?> DownloadCoverArtAsync(string coverArtUrl)
        {
            try
            {
                var response = await _httpClient.GetAsync(coverArtUrl);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync();
                }
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                _logger.LogError(ex, "Error downloading cover art from: {Url}", LogRedaction.SanitizeUrl(coverArtUrl));
                return null;
            }
        }

        private AudioMetadata? ParseAudnexusResponse(JsonElement audnexusData)
        {
            // This is a simplified parser - adapt based on actual Audnexus API response structure
            var metadata = new AudioMetadata();

            if (audnexusData.TryGetProperty("title", out var title))
                metadata.Title = title.GetString() ?? "";

            if (audnexusData.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
            {
                var authorNames = authors.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(a.GetString())).Select(a => a.GetString()!);
                metadata.Artist = string.Join(", ", authorNames);
            }

            if (audnexusData.TryGetProperty("series", out var series))
                metadata.Series = series.GetString();

            if (audnexusData.TryGetProperty("publishedYear", out var year))
                metadata.Year = year.GetInt32();

            if (audnexusData.TryGetProperty("description", out var description))
                metadata.Description = description.GetString();

            if (audnexusData.TryGetProperty("isbn", out var isbn))
                metadata.Isbn = isbn.GetString();

            if (audnexusData.TryGetProperty("coverUrl", out var coverUrl))
                metadata.CoverArtUrl = coverUrl.GetString();

            return metadata;
        }
    }
}

