using System.Text.Json;
using Cascade.CTL.Agent.Domain.Contracts;
using Cascade.CTL.Agent.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Cascade.CTL.Agent.Infrastructure.RAG.Query;

//graceful fallback if Azure Search fails to initialize (bad key, network issue, quota)
public sealed class InMemoryRAGService : IRAGQueryService
{
    private readonly ILogger<InMemoryRAGService> _logger;
    private readonly List<RAGDocument> _documents;

    public InMemoryRAGService(ILogger<InMemoryRAGService> logger, string? ragKnowledgePath = null)
    {
        _logger = logger;
        _documents = LoadDocuments(ragKnowledgePath);
    }

    public Task<RAGQueryResult> QueryAsync(
        string query,
        string? stateCode = null,
        string? county = null,
        string? assetType = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("InMemoryRAG: Querying for '{Query}' [State={State}, County={County}, AssetType={AssetType}]",
            query, stateCode, county, assetType);

        var filtered = _documents.AsEnumerable();

        if (!string.IsNullOrEmpty(stateCode))
            filtered = filtered.Where(d =>
                string.IsNullOrEmpty(d.State) ||
                d.State.Equals(stateCode, StringComparison.OrdinalIgnoreCase) ||
                d.State.Equals("ALL", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(county))
            filtered = filtered.Where(d =>
                string.IsNullOrEmpty(d.County) ||
                d.County.Equals(county, StringComparison.OrdinalIgnoreCase) ||
                d.County.Equals("ALL", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(assetType))
            filtered = filtered.Where(d =>
                string.IsNullOrEmpty(d.AssetType) ||
                d.AssetType.Equals(assetType, StringComparison.OrdinalIgnoreCase) ||
                d.AssetType.Equals("ALL", StringComparison.OrdinalIgnoreCase));

        var queryTerms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scored = filtered.Select(d =>
        {
            var contentLower = d.Content.ToLowerInvariant();
            var titleLower = d.Title.ToLowerInvariant();
            double score = 0;
            foreach (var term in queryTerms)
            {
                var termLower = term.ToLowerInvariant();
                if (titleLower.Contains(termLower)) score += 0.3;
                var occurrences = CountOccurrences(contentLower, termLower);
                score += occurrences * 0.1;
            }
            return d with { RelevanceScore = Math.Min(score, 1.0) };
        })
        .Where(d => d.RelevanceScore > 0.05)
        .OrderByDescending(d => d.RelevanceScore)
        .Take(5)
        .ToArray();

        var result = new RAGQueryResult
        {
            Query = query,
            Documents = scored,
            TotalMatches = scored.Length
        };

        _logger.LogInformation("InMemoryRAG: Found {Count} matching documents", scored.Length);
        return Task.FromResult(result);
    }

    private static int CountOccurrences(string text, string term)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            count++;
            index += term.Length;
        }
        return count;
    }

    private List<RAGDocument> LoadDocuments(string? ragKnowledgePath)
    {
        var documents = new List<RAGDocument>();

        if (!string.IsNullOrEmpty(ragKnowledgePath) && Directory.Exists(ragKnowledgePath))
        {
            foreach (var file in Directory.GetFiles(ragKnowledgePath, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var doc = JsonSerializer.Deserialize<RAGDocument>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (doc != null)
                        documents.Add(doc);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load RAG document from {File}", file);
                }
            }
        }

        if (documents.Count == 0)
        {
            _logger.LogInformation("No external RAG documents found, loading built-in policy documents");
            documents.AddRange(GetBuiltInDocuments());
        }

        _logger.LogInformation("InMemoryRAG: Loaded {Count} documents", documents.Count);
        return documents;
    }

    private static List<RAGDocument> GetBuiltInDocuments() =>
    [
        new RAGDocument
        {
            Id = "CTL-POLICY-001",
            Title = "General CTL Requirements — All States Baseline",
            Content = """
                Clear-To-List (CTL) Policy — Baseline Requirements (All States):
                1. Title must be clear of all material liens and encumbrances that would prevent transfer.
                2. A BPO (Broker Price Opinion) must exist and must not be stale (>90 days old requires refresh).
                3. Property occupancy status must be verified within the last 30 days.
                4. If property is occupied, eviction must be completed or cash-for-keys must be executed before listing.
                5. All critical code violations must be resolved before listing. Minor violations may be listed with disclosure.
                6. HOA delinquencies must be resolved or a payment plan must be in place.
                7. AVM variance from BPO should not exceed 15%. Variance above 15% requires valuation review.
                8. For Tier 1 sellers, all conditions must be satisfied before listing. Tier 2/3 sellers allow conditional listing.
                9. Confidence score >= 0.90 indicates Clear. Score 0.75-0.89 indicates ClearWithConditions. Score < 0.75 requires human review.
                """,
            RelevanceScore = 0,
            State = "ALL",
            County = "ALL",
            AssetType = "ALL",
            PolicyType = "CTL-Baseline"
        },
        new RAGDocument
        {
            Id = "CTL-POLICY-TX-001",
            Title = "Texas Foreclosure CTL Requirements",
            Content = """
                Texas Foreclosure CTL Policy — State-Specific Requirements:
                1. Texas Property Code Section 51.002 requires proper notice of sale. Verify foreclosure sale was conducted per statute.
                2. Texas has no statutory right of redemption for foreclosure sales — title transfers immediately at auction.
                3. HOA foreclosure: Texas Property Code Section 209 governs HOA assessment liens. Verify HOA lien priority.
                4. Tax liens filed by county tax assessor take priority over all other liens in Texas.
                5. For Dallas County: additional requirement to verify no pending eminent domain proceedings.
                6. BPO staleness threshold for Texas foreclosures: 60 days (stricter than baseline 90 days).
                7. Occupancy requirement: If vacant, property must be secured and winterized. Post notice of ownership change.
                8. Texas does not require seller disclosure for foreclosure properties (exemption under Texas Property Code 5.008(e)).
                9. For Tier 1 sellers in Texas: expedited CTL — all checks must complete within 48 hours of ingestion.
                """,
            RelevanceScore = 0,
            State = "TX",
            County = "ALL",
            AssetType = "Foreclosure",
            PolicyType = "CTL-State"
        },
        new RAGDocument
        {
            Id = "CTL-POLICY-CA-001",
            Title = "California REO Listing Requirements",
            Content = """
                California REO CTL Policy — State-Specific Requirements:
                1. California Civil Code Section 2924 governs non-judicial foreclosure. Verify proper trustee's sale.
                2. California has a statutory right of redemption: 1 year for judicial foreclosure, none for non-judicial.
                3. REO properties in California require mandatory hazard disclosures (earthquakes, floods, fire zones).
                4. California's SB-1079 grants tenants and nonprofits right of first refusal on foreclosed 1-4 unit properties.
                5. For Los Angeles County: Rent Stabilization Ordinance (RSO) must be verified for multi-unit properties.
                6. BPO staleness threshold for California REO: 90 days (standard baseline).
                7. Occupancy: California requires formal unlawful detainer proceedings. Self-help eviction is prohibited.
                8. AVM variance threshold for California: 10% (stricter than 15% baseline due to volatile market).
                9. All properties in California must have Natural Hazard Disclosure (NHD) report before listing.
                10. HOA delinquency in California: HOA super lien priority per California Civil Code 5680.
                """,
            RelevanceScore = 0,
            State = "CA",
            County = "ALL",
            AssetType = "REO",
            PolicyType = "CTL-State"
        },
        new RAGDocument
        {
            Id = "CTL-POLICY-HOA-001",
            Title = "HOA Verification Policies — All States",
            Content = """
                HOA Verification Policy — CTL Requirements:
                1. If property is within an HOA, verify current assessment status.
                2. Delinquent HOA assessments must be resolved before listing or placed on payment plan.
                3. HOA delinquency > $5,000 is a CTL blocker — requires resolution before listing.
                4. HOA delinquency $1,000-$5,000 allows listing with ClearWithConditions verdict and disclosure.
                5. HOA delinquency < $1,000 is not a CTL blocker — proceed with standard listing.
                6. Verify HOA transfer fees and obtain estoppel certificate requirements.
                7. Special assessments pending or recently levied must be disclosed to buyers.
                8. For states with HOA super lien priority (e.g., CA, FL, NV): verify lien position carefully.
                """,
            RelevanceScore = 0,
            State = "ALL",
            County = "ALL",
            AssetType = "ALL",
            PolicyType = "HOA-Verification"
        },
        new RAGDocument
        {
            Id = "CTL-POLICY-VAL-001",
            Title = "Valuation Staleness and Confidence Thresholds",
            Content = """
                Valuation Policy — CTL Requirements:
                1. BPO must exist for all assets. Missing BPO is a CTL blocker (NeedsHumanReview).
                2. BPO staleness thresholds: Standard 90 days. TX Foreclosure: 60 days. CA REO: 90 days. FL: 90 days.
                3. If BPO is stale, a new BPO must be ordered. Stale BPO alone triggers ClearWithConditions.
                4. AVM should be obtained as secondary valuation to cross-reference BPO.
                5. AVM variance from BPO thresholds: Standard: 15%. CA: 10%. TX: 15%. FL: 12%.
                6. If AVM variance exceeds threshold, valuation review by asset manager is required (NeedsHumanReview).
                7. AVM confidence score < 0.70 means AVM is unreliable — rely on BPO only, note in findings.
                8. For properties with no BPO and no reliable AVM: NeedsHumanReview verdict is mandatory.
                9. BPO quality rating must be 'Medium' or higher. 'Low' quality BPO triggers ClearWithConditions.
                """,
            RelevanceScore = 0,
            State = "ALL",
            County = "ALL",
            AssetType = "ALL",
            PolicyType = "Valuation"
        },
        new RAGDocument
        {
            Id = "CTL-POLICY-OCC-001",
            Title = "Occupancy Clearance Policies",
            Content = """
                Occupancy Policy — CTL Requirements:
                1. Vacant property with confirmed vacancy within last 30 days: Clear for listing.
                2. Vacant property with stale inspection (>30 days): ClearWithConditions — require re-inspection.
                3. Occupied property with completed eviction: Clear for listing after vacancy confirmation.
                4. Occupied property with eviction in progress: Not clear — ClearWithConditions only if eviction expected within 30 days.
                5. Occupied property with no eviction filed: Not clear for listing. NeedsHumanReview.
                6. Unknown occupancy status: NeedsHumanReview — require field service inspection.
                7. Property condition rated 'Poor' or 'Hazardous': NeedsHumanReview for repair assessment.
                8. Property condition rated 'Fair' or 'Good': Clear for listing with condition disclosure.
                9. Cash-for-keys program: If tenant has accepted cash-for-keys, treat as Clear with move-out date condition.
                10. Squatter/unauthorized occupant: NeedsHumanReview — requires legal action before listing.
                """,
            RelevanceScore = 0,
            State = "ALL",
            County = "ALL",
            AssetType = "ALL",
            PolicyType = "Occupancy"
        }
    ];
}
