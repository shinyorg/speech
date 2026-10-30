namespace Shiny.AiConversation;

/// <summary>
/// A stored binary (image) referenced from a message. Binary content is never inlined in message documents.
/// </summary>
public record AttachmentRef(string AttachmentId, string MediaType, long Length);

/// <summary>
/// How long the model keeps seeing an image.
/// </summary>
public enum ImageRetention
{
    /// <summary>
    /// The image goes to the model with the turn it arrived in; later prompts only mention it. Stored for history.
    /// </summary>
    DropAfterTurn,

    /// <summary>
    /// The image stays in every prompt until compacted (costs tokens on every turn).
    /// </summary>
    KeepInContext,

    /// <summary>
    /// The image is never stored: it goes to the model once, from memory, and is gone afterwards.
    /// </summary>
    DontStore
}

/// <summary>
/// An image sent with an <see cref="AiInput"/>. The session stores it and replaces it with an <see cref="ImagePart"/>.
/// </summary>
public record AiImage(ReadOnlyMemory<byte> Data, string MediaType, string? Description = null, ImageRetention Retention = ImageRetention.DropAfterTurn);

/// <summary>
/// Stores message attachments. <see cref="InMemoryAiAttachmentStore"/> is the default; <see cref="FileAiAttachmentStore"/>
/// keeps them in a directory (e.g. the app data folder).
/// </summary>
public interface IAiAttachmentStore
{
    Task<AttachmentRef> Save(ReadOnlyMemory<byte> content, string mediaType, string sessionId, CancellationToken ct = default);
    Task<byte[]?> Load(string attachmentId, CancellationToken ct = default);
    Task Delete(string attachmentId, CancellationToken ct = default);
    Task DeleteForSession(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// A URI a UI can show the image from (e.g. a file path), or null when there is none.
    /// </summary>
    string? GetDisplayUri(string attachmentId) => null;
}


public class InMemoryAiAttachmentStore : IAiAttachmentStore
{
    readonly Lock sync = new();
    readonly Dictionary<string, (string SessionId, byte[] Data)> items = new();

    public Task<AttachmentRef> Save(ReadOnlyMemory<byte> content, string mediaType, string sessionId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (this.sync)
            this.items[id] = (sessionId, content.ToArray());

        return Task.FromResult(new AttachmentRef(id, mediaType, content.Length));
    }

    public Task<byte[]?> Load(string attachmentId, CancellationToken ct = default)
    {
        lock (this.sync)
            return Task.FromResult(this.items.TryGetValue(attachmentId, out var item) ? item.Data : null);
    }

    public Task Delete(string attachmentId, CancellationToken ct = default)
    {
        lock (this.sync)
            this.items.Remove(attachmentId);

        return Task.CompletedTask;
    }

    public Task DeleteForSession(string sessionId, CancellationToken ct = default)
    {
        lock (this.sync)
        {
            foreach (var key in this.items.Where(x => x.Value.SessionId == sessionId).Select(x => x.Key).ToList())
                this.items.Remove(key);
        }
        return Task.CompletedTask;
    }
}


/// <summary>
/// Stores attachments as files under <c>{directory}/{sessionId}/{attachmentId}</c>. Encrypt the directory
/// (or use a custom store) if conversations are sensitive.
/// </summary>
public class FileAiAttachmentStore(string directory) : IAiAttachmentStore
{
    public async Task<AttachmentRef> Save(ReadOnlyMemory<byte> content, string mediaType, string sessionId, CancellationToken ct = default)
    {
        var id = $"{Sanitize(sessionId)}/{Guid.NewGuid():N}{Extension(mediaType)}";
        var path = this.PathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content.ToArray(), ct).ConfigureAwait(false);
        return new AttachmentRef(id, mediaType, content.Length);
    }

    public async Task<byte[]?> Load(string attachmentId, CancellationToken ct = default)
    {
        var path = this.PathFor(attachmentId);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
    }

    public Task Delete(string attachmentId, CancellationToken ct = default)
    {
        var path = this.PathFor(attachmentId);
        if (File.Exists(path))
            File.Delete(path);

        return Task.CompletedTask;
    }

    public Task DeleteForSession(string sessionId, CancellationToken ct = default)
    {
        var dir = Path.Combine(directory, Sanitize(sessionId));
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);

        return Task.CompletedTask;
    }

    public string? GetDisplayUri(string attachmentId)
    {
        var path = this.PathFor(attachmentId);
        return File.Exists(path) ? path : null;
    }

    string PathFor(string attachmentId)
    {
        var parts = attachmentId.Split('/', 2);
        if (parts.Length != 2 || parts.Any(p => p.Contains("..") || p.Contains('\\')))
            throw new ArgumentException("Invalid attachment id", nameof(attachmentId));

        return Path.Combine(directory, parts[0], parts[1]);
    }

    static string Sanitize(string value)
        => String.Concat(value.Select(c => Char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    static string Extension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/heic" => ".heic",
        "image/gif" => ".gif",
        _ => ""
    };
}


/// <summary>
/// Something holding the microphone or speaker (the voice conversation) that must let go while another
/// feature, such as the camera, needs them.
/// </summary>
public interface IAudioFocusParticipant
{
    Task Pause();
    Task Resume();
}
