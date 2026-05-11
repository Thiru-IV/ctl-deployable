using System.Collections.Concurrent;
using Microsoft.Bot.Schema;

namespace Cascade.CTL.Agent.Api.Teams;

/// <summary>
/// Stores Bot Framework <see cref="ConversationReference"/>s captured the first time a user
/// messages the bot. A reference is required before the agent can proactively DM that user.
/// </summary>
public interface IConversationReferenceStore
{
    void Save(string userKey, ConversationReference reference);
    ConversationReference? Get(string userKey);
    IEnumerable<ConversationReference> All();
}

/// <summary>
/// In-memory store. Sufficient for the dev-tenant POC. For production, swap with a
/// Cosmos / Table Storage implementation so references survive container restarts.
/// </summary>
public sealed class InMemoryConversationReferenceStore : IConversationReferenceStore
{
    private readonly ConcurrentDictionary<string, ConversationReference> _refs =
        new(StringComparer.OrdinalIgnoreCase);

    public void Save(string userKey, ConversationReference reference) =>
        _refs[userKey] = reference;

    public ConversationReference? Get(string userKey) =>
        _refs.TryGetValue(userKey, out var r) ? r : null;

    public IEnumerable<ConversationReference> All() => _refs.Values;
}
