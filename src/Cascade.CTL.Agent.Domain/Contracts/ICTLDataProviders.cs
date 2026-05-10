using Cascade.CTL.Agent.Domain.Models;

namespace Cascade.CTL.Agent.Domain.Contracts;

public interface ITitleSearchProvider
{
    Task<TitleSearchResult> SearchAsync(string parcelId, string stateCode, CancellationToken cancellationToken = default);
}

public interface IHOAProvider
{
    Task<HOAResult> CheckDelinquencyAsync(string propertyAddress, CancellationToken cancellationToken = default);
}

public interface ICodeViolationProvider
{
    Task<CodeViolationResult> LookupAsync(string propertyAddress, string county, CancellationToken cancellationToken = default);
}

public interface IBPOProvider
{
    Task<BPOResult> RetrieveAsync(string assetId, CancellationToken cancellationToken = default);
}

public interface IAVMProvider
{
    Task<AVMResult> GetValuationAsync(string propertyAddress, string stateCode, CancellationToken cancellationToken = default);
}

public interface IOccupancyProvider
{
    Task<OccupancyStatusResult> GetStatusAsync(string propertyAddress, CancellationToken cancellationToken = default);
}

public interface IAssetProfileProvider
{
    Task<Asset> GetAssetProfileAsync(string assetId, CancellationToken cancellationToken = default);
}

public interface IRAGQueryService
{
    Task<RAGQueryResult> QueryAsync(string query, string? stateCode = null, string? county = null, string? assetType = null, CancellationToken cancellationToken = default);
}
