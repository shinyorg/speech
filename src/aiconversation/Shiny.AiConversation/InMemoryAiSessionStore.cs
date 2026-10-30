namespace Shiny.AiConversation;

/// <summary>
/// Keeps sessions in memory only. The default store; useful for tests and apps that don't need history
/// to survive a restart.
/// </summary>
public class InMemoryAiSessionStore : IAiSessionStore
{
    readonly Lock sync = new();
    readonly Dictionary<string, AiSessionDocument> sessions = new();
    readonly Dictionary<string, List<AiMessageDocument>> messages = new();
    readonly Dictionary<string, long> sequences = new();

    public Task CreateSession(AiSessionDocument session, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            if (!this.sessions.TryAdd(session.SessionId, session))
                throw new InvalidOperationException($"Session '{session.SessionId}' already exists");
        }
        return Task.CompletedTask;
    }

    public Task SaveSession(AiSessionDocument session, long expectedVersion, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            if (!this.sessions.TryGetValue(session.SessionId, out var current) || current.Version != expectedVersion)
                throw new SessionConflictException(session.SessionId);

            this.sessions[session.SessionId] = session;
        }
        return Task.CompletedTask;
    }

    public Task<AiSessionDocument?> GetSession(string sessionId, CancellationToken ct = default)
    {
        lock (this.sync)
            return Task.FromResult(this.sessions.GetValueOrDefault(sessionId));
    }

    public Task<IReadOnlyList<AiSessionDocument>> QuerySessions(SessionQuery query, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            IEnumerable<AiSessionDocument> result = this.sessions.Values;

            if (!String.IsNullOrWhiteSpace(query.Text))
                result = result.Where(x => Contains(x.Title, query.Text) || Contains(x.Summary, query.Text));

            if (query.From != null)
                result = result.Where(x => x.LastActivity >= query.From);

            if (query.To != null)
                result = result.Where(x => x.LastActivity <= query.To);

            if (query.Tags is { Count: > 0 })
                result = result.Where(x => query.Tags.All(t => x.Settings.Tags.Contains(t)));

            if (query.Profile != null)
                result = result.Where(x => x.Settings.Profile == query.Profile);

            IReadOnlyList<AiSessionDocument> list = result
                .OrderByDescending(x => x.LastActivity)
                .Skip(query.Skip)
                .Take(query.Take)
                .ToList();

            return Task.FromResult(list);
        }
    }

    public Task DeleteSession(string sessionId, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            this.sessions.Remove(sessionId);
            this.messages.Remove(sessionId);
            this.sequences.Remove(sessionId);
        }
        return Task.CompletedTask;
    }

    public Task<AiMessageDocument> AppendMessage(AiMessageDocument message, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            var seq = this.sequences.GetValueOrDefault(message.SessionId) + 1;
            this.sequences[message.SessionId] = seq;

            var stored = message with { Sequence = seq };
            if (!this.messages.TryGetValue(message.SessionId, out var list))
            {
                list = [];
                this.messages[message.SessionId] = list;
            }
            list.Add(stored);

            return Task.FromResult(stored);
        }
    }

    public Task UpdateMessage(AiMessageDocument message, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            if (this.messages.TryGetValue(message.SessionId, out var list))
            {
                var index = list.FindIndex(x => x.MessageId == message.MessageId);
                if (index >= 0)
                    list[index] = message;
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AiMessageDocument>> GetMessages(MessageQuery query, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            if (!this.messages.TryGetValue(query.SessionId, out var list))
                return Task.FromResult<IReadOnlyList<AiMessageDocument>>([]);

            IEnumerable<AiMessageDocument> result = list;
            if (!query.IncludeCompacted)
                result = result.Where(x => !x.Compacted);

            if (query.AfterSequence != null)
                result = result.Where(x => x.Sequence > query.AfterSequence);

            if (query.BeforeSequence != null)
                result = result.Where(x => x.Sequence < query.BeforeSequence);

            result = result.OrderBy(x => x.Sequence);
            if (query.Limit != null)
            {
                // paging backwards takes the newest messages before the cursor; otherwise the oldest after it
                result = query.BeforeSequence != null || query.AfterSequence == null
                    ? result.TakeLast(query.Limit.Value)
                    : result.Take(query.Limit.Value);
            }

            IReadOnlyList<AiMessageDocument> page = result.ToList();
            return Task.FromResult(page);
        }
    }

    public Task<IReadOnlyList<MessageSearchHit>> SearchMessages(MessageSearchQuery query, CancellationToken ct = default)
    {
        var terms = SearchText.Terms(query.Text);
        if (terms.Length == 0)
            return Task.FromResult<IReadOnlyList<MessageSearchHit>>([]);

        lock (this.sync)
        {
            var hits = new List<MessageSearchHit>();
            foreach (var (sessionId, list) in this.messages)
            {
                if (query.SessionId != null && sessionId != query.SessionId)
                    continue;

                if (query.ExcludeSessionId != null && sessionId == query.ExcludeSessionId)
                    continue;

                var session = this.sessions.GetValueOrDefault(sessionId);
                if (session?.Settings.ExcludeFromSearch == true)
                    continue;

                foreach (var msg in list)
                {
                    if (msg.Role is not (MessageRole.User or MessageRole.Assistant) || String.IsNullOrEmpty(msg.Text))
                        continue;

                    if (query.From != null && msg.Timestamp < query.From)
                        continue;

                    if (query.To != null && msg.Timestamp > query.To)
                        continue;

                    if (query.ExcludeFromSequence != null && msg.Sequence >= query.ExcludeFromSequence)
                        continue;

                    var matched = terms.Count(t => SearchText.ContainsTerm(msg.Text, t));
                    if (matched == 0)
                        continue;

                    hits.Add(new MessageSearchHit(
                        sessionId,
                        session?.Title,
                        msg.Sequence,
                        msg.Role,
                        msg.Timestamp,
                        SearchText.Snippet(msg.Text, terms),
                        (double)matched / terms.Length
                    ));
                }
            }

            IReadOnlyList<MessageSearchHit> result = hits
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Timestamp)
                .Take(query.Limit)
                .ToList();

            return Task.FromResult(result);
        }
    }

    public Task Purge(DateTimeOffset before, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            var ids = this.sessions.Values.Where(x => x.LastActivity < before).Select(x => x.SessionId).ToList();
            foreach (var id in ids)
            {
                this.sessions.Remove(id);
                this.messages.Remove(id);
                this.sequences.Remove(id);
            }
        }
        return Task.CompletedTask;
    }

    static bool Contains(string? text, string value)
        => text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
}
