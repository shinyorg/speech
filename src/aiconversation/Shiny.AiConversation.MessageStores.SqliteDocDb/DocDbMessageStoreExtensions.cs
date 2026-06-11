using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AiConversation;
using Shiny.AiConversation.MessageStores.SqliteDocDb;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;

namespace Shiny;

public static class DocDbMessageStoreExtensions
{
    public const string SqliteDiRegKey = "ShinyAiConvMessageStoreDocDb";

    /// <summary>
    /// Uses an already-registered named IDocumentStore as the message store.
    /// </summary>
    public static AiConversationOptions SetDocDbMessageStore(this AiConversationOptions options, string documentStoreKey)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(documentStoreKey);

        options.Services.AddSingleton<IMessageStore>(sp =>
            new DocumentDbMessageStore(sp.GetRequiredService<IDocumentStoreProvider>().GetStore(documentStoreKey))
        );
        return options;
    }

    public static AiConversationOptions SetSqliteDocDbMessageStore(this AiConversationOptions options, string databasePath)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(databasePath);

        var connString = databasePath;
        if (!connString.StartsWith("Data Source="))
            connString = "Data Source=" + connString;

        options.Services.AddDocumentStore(SqliteDiRegKey, opts =>
        {
            opts.UseReflectionFallback = false;
            opts.JsonSerializerOptions = AppJsonContext.Default.Options;
            opts.DatabaseProvider = new SqliteDatabaseProvider(connString);
        });
        return options.SetDocDbMessageStore(SqliteDiRegKey);
    }
}

[JsonSerializable(typeof(AiChatMessage))]
public partial class AppJsonContext : JsonSerializerContext;