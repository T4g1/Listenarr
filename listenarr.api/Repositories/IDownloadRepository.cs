using System.Threading.Tasks;
using System.Collections.Generic;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Repositories
{
    public interface IDownloadRepository
    {
        Task AddAsync(Download download);
        Task<Download?> FindAsync(string id);
        Task UpdateAsync(Download download);
        Task UpdateMetadataAsync(string id, string key, object? value);
        Task RemoveAsync(string id);
        Task<List<Download>> GetAllAsync();
        Task<List<Download>> GetQueueDisplayCandidatesAsync();
        Task<List<Download>> GetQueueMatchingCandidatesAsync();
        Task<List<string>> GetKnownClientItemIdsAsync();
        Task<List<Download>> GetByClientAsync(string clientId);
        Task<List<Download>> GetByIdsAsync(IEnumerable<string> ids);
    }
}
