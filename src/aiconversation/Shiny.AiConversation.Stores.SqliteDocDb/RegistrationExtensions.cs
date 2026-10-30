using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AiConversation;
using Shiny.AiConversation.Stores.SqliteDocDb;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;

namespace Shiny;

public static class AiConversationDocumentStoreExtensions
{
    public const string SqliteDiRegKey = "ShinyAiConversationStores";


    /// <summary>
    /// Stores sessions, messages and assistant memory in a SQLite database at <paramref name="databasePath"/>.
    /// </summary>
    public static AiConversationOptions SetSqliteDocDbStores(this AiConversationOptions options, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var connString = databasePath.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
            ? databasePath
            : "Data Source=" + databasePath;

        options.Services.AddDocumentStore(SqliteDiRegKey, opts =>
        {
            opts.UseReflectionFallback = false;
            opts.DatabaseProvider = new SqliteDatabaseProvider(connString);
            opts.MapAiConversation();
        });
        return options.SetDocDbStores(SqliteDiRegKey);
    }


    /// <summary>
    /// Uses an already-registered named document store for sessions and memory. Its options must include
    /// <see cref="MapAiConversation"/>.
    /// </summary>
    public static AiConversationOptions SetDocDbStores(this AiConversationOptions options, string documentStoreKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentStoreKey);

        options.Services.TryAddSingleton<IAiSessionStore>(sp =>
            new DocumentDbAiSessionStore(sp.GetRequiredService<IDocumentStoreProvider>().GetStore(documentStoreKey))
        );
        options.Services.TryAddSingleton<IAiMemoryStore>(sp =>
            new DocumentDbAiMemoryStore(sp.GetRequiredService<IDocumentStoreProvider>().GetStore(documentStoreKey))
        );
        return options;
    }


    /// <summary>
    /// Adds what the AI conversation stores need to a document store: JSON metadata, optimistic concurrency
    /// on session headers, sequence counters and memory documents, and full-text search on message text.
    /// </summary>
    public static DocumentStoreOptions MapAiConversation(this DocumentStoreOptions opts)
    {
        var resolver = AiConversationStoreJsonContext.Default;
        opts.JsonSerializerOptions = opts.JsonSerializerOptions is { } existing
            ? new JsonSerializerOptions(existing)
            {
                TypeInfoResolver = existing.TypeInfoResolver == null
                    ? resolver
                    : JsonTypeInfoResolver.Combine(existing.TypeInfoResolver, resolver)
            }
            : resolver.Options;

        opts.MapVersionProperty<StoredAiSession>(nameof(StoredAiSession.Version), x => x.Version, (x, v) => x.Version = v);
        opts.MapVersionProperty<StoredAiSequence>(nameof(StoredAiSequence.Version), x => x.Version, (x, v) => x.Version = v);
        opts.MapVersionProperty<StoredAiMemory>(nameof(StoredAiMemory.Version), x => x.Version, (x, v) => x.Version = v);
        opts.MapFullTextProperty<StoredAiMessage>(x => x.Text!, FullTextLanguage.English);
        return opts;
    }
}
