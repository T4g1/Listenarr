using AsyncKeyedLock;

namespace Listenarr.Api.Services.Metadata
{
    public class MetadataExtractionLimiter
    {
        // Default concurrent ffprobe extractions
        public AsyncNonKeyedLocker Sem { get; } = new(4);
    }
}
