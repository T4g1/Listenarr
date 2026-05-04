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
using AsyncKeyedLock;
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Caching.Memory;

namespace Listenarr.Api.Services;

public class RemotePathMappingService : IRemotePathMappingService
{
    private readonly IRemotePathMappingRepository _mappings;
    private readonly ILogger<RemotePathMappingService> _logger;
    private readonly IMemoryCache _cache;
    private static readonly AsyncKeyedLocker<string> _locker = new();

    public RemotePathMappingService(
        IRemotePathMappingRepository mappings,
        ILogger<RemotePathMappingService> logger,
        IMemoryCache cache)
    {
        _mappings = mappings;
        _logger = logger;
        _cache = cache;
    }

    public async Task<List<RemotePathMapping>> GetAllAsync()
    {
        var all = await _mappings.GetAllAsync();
        return all.OrderBy(m => m.DownloadClientId).ThenBy(m => m.Name).ToList();
    }

    public async Task<RemotePathMapping?> GetByIdAsync(int id)
    {
        return await _mappings.GetByIdAsync(id);
    }

    public async Task<List<RemotePathMapping>> GetPathMappingByClientIdAsync(string downloadClientId)
    {
        if (string.IsNullOrEmpty(downloadClientId)) return new List<RemotePathMapping>();
        var cacheKey = $"rpm_client_{downloadClientId}";

        if (_cache.TryGetValue<List<RemotePathMapping>>(cacheKey, out var mappings))
        {
            return mappings ?? [];
        }

        using var _ = await _locker.LockAsync(cacheKey);

        mappings = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
            return await _mappings.GetByClientAsync(downloadClientId);
        });

        return mappings ?? [];
    }

    public async Task<RemotePathMapping> CreateAsync(RemotePathMapping mapping)
    {
        mapping.NormalizePaths();
        mapping.CreatedAt = DateTime.UtcNow;
        mapping.UpdatedAt = DateTime.UtcNow;

        var saved = await _mappings.SaveAsync(mapping);

        _logger.LogInformation(
            "Created remote path mapping {MappingId} for client {ClientId}: {RemotePath} -> {LocalPath}",
            saved.Id, saved.DownloadClientId, saved.RemotePath, saved.LocalPath);

        try { _cache.Remove($"rpm_client_{saved.DownloadClientId}"); }
        catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
        }

        return saved;
    }

    public async Task<RemotePathMapping> UpdateAsync(RemotePathMapping mapping)
    {
        var existing = await _mappings.GetByIdAsync(mapping.Id);
        if (existing == null)
        {
            throw new KeyNotFoundException($"Remote path mapping with ID {mapping.Id} not found");
        }

        mapping.NormalizePaths();
        mapping.CreatedAt = existing.CreatedAt;
        mapping.UpdatedAt = DateTime.UtcNow;

        var saved = await _mappings.SaveAsync(mapping);

        _logger.LogInformation(
            "Updated remote path mapping {MappingId} for client {ClientId}: {RemotePath} -> {LocalPath}",
            saved.Id, saved.DownloadClientId, saved.RemotePath, saved.LocalPath);

        try { _cache.Remove($"rpm_client_{saved.DownloadClientId}"); }
        catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
        }

        return saved;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var existing = await _mappings.GetByIdAsync(id);
        if (existing == null) return false;

        var deleted = await _mappings.DeleteAsync(id);

        if (deleted)
        {
            _logger.LogInformation(
                "Deleted remote path mapping {MappingId} for client {ClientId}",
                id, existing.DownloadClientId);

            try { _cache.Remove($"rpm_client_{existing.DownloadClientId}"); }
            catch (Exception caughtEx_3) when (caughtEx_3 is not OperationCanceledException && caughtEx_3 is not OutOfMemoryException && caughtEx_3 is not StackOverflowException)
            {
                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
            }
        }

        return deleted;
    }

    public async Task<string> TranslatePathAsync(string downloadClientId, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return remotePath;
        }

        remotePath = FileUtils.NormalizeStoredPath(remotePath);

        // We cannot make sure the given path is a file or a directory as it is possibly unnaccessible in its current form
        // thus we try the mapping on the unmodified given path and then we try to map as if it were a directory
        string[] tryingRemotePaths = [
            remotePath,
            FileUtils.EnsureTrailingSeparator(remotePath)
        ];

        foreach (var currentRemotePath in tryingRemotePaths)
        {
            var mappings = await GetPathMappingByClientIdAsync(downloadClientId);
            foreach (var mapping in mappings)
            {
                if (currentRemotePath.StartsWith(mapping.RemotePath, StringComparison.OrdinalIgnoreCase))
                {
                    return currentRemotePath.Replace(mapping.RemotePath, mapping.LocalPath, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return remotePath;
    }

    public async Task<bool> RequiresTranslationAsync(string downloadClientId, string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            return false;
        }

        var normalizedRemotePath = FileUtils.NormalizeStoredPath(remotePath);

        var mappings = await GetPathMappingByClientIdAsync(downloadClientId);
        return mappings.Any(m => normalizedRemotePath.StartsWith(FileUtils.NormalizeStoredPath(m.RemotePath)));
    }
}
