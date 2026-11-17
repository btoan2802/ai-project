namespace BtoanAiNet.Services.VectorDb
{
    /// <summary>
    /// Represents a high-dimensional vector point stored in the vector database with metadata payload.
    /// </summary>
    public class VectorPoint
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public float[] Vector { get; set; } = Array.Empty<float>();
        public Dictionary<string, object> Payload { get; set; } = new();
    }

    /// <summary>
    /// Represents a nearest-neighbor semantic search match returned by the vector store.
    /// </summary>
    public class VectorSearchResult
    {
        public string Id { get; set; } = string.Empty;
        public float Score { get; set; }
        public Dictionary<string, object> Payload { get; set; } = new();

        public T? GetPayloadValue<T>(string key)
        {
            if (Payload.TryGetValue(key, out var val))
            {
                if (val is T target) return target;
                if (val != null)
                {
                    try
                    {
                        return (T)Convert.ChangeType(val, typeof(T));
                    }
                    catch { }
                }
            }
            return default;
        }
    }

    /// <summary>
    /// Abstraction contract for Vector Database operations (e.g., Qdrant) powering RAG workflows.
    /// </summary>
    public interface IVectorDatabaseService
    {
        /// <summary>
        /// Ensures the target vector collection exists with the specified dimension and distance metric.
        /// </summary>
        Task EnsureCollectionAsync(string collectionName, int vectorDimension = 768, CancellationToken cancellationToken = default);

        /// <summary>
        /// Inserts or updates batch vector points into the collection.
        /// </summary>
        Task UpsertPointsAsync(string collectionName, IEnumerable<VectorPoint> points, CancellationToken cancellationToken = default);

        /// <summary>
        /// Performs approximate nearest neighbor (ANN) cosine similarity search against index embeddings.
        /// </summary>
        Task<List<VectorSearchResult>> SearchAsync(string collectionName, float[] queryVector, int topK = 5, float minScore = 0.60f, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes points matching a specific payload metadata key-value pair.
        /// </summary>
        Task DeleteByFilterAsync(string collectionName, string filterKey, object filterValue, CancellationToken cancellationToken = default);

        /// <summary>
        /// Verifies network connectivity and cluster health of the vector engine.
        /// </summary>
        Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
    }
}
