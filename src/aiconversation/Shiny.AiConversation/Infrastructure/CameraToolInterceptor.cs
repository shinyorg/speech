using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shiny.AiConversation.Infrastructure;

/// <summary>
/// The <c>use_camera</c> tool (D14). Photos reach the model either inline in the tool result (providers with
/// <see cref="IChatClientProvider.SupportsImagesInToolResults"/>) or as a follow-up message in the same turn.
/// The voice conversation (any <see cref="IAudioFocusParticipant"/>) is paused while the camera is open.
/// </summary>
public class CameraToolInterceptor(
    ICameraCapture capture,
    IChatClientProvider chatClientProvider,
    IEnumerable<IAudioFocusParticipant> audioFocus,
    CameraToolOptions options,
    ICameraConsent? consent = null,
    ILogger<CameraToolInterceptor>? logger = null
) : ISessionInterceptor
{
    public const string ToolName = "use_camera";

    readonly IReadOnlyList<IAudioFocusParticipant> focus = audioFocus.ToList();
    readonly ILogger log = logger ?? NullLogger<CameraToolInterceptor>.Instance;


    public Task OnBeforeTurn(TurnContext context)
    {
        if (!ProfileFilter.Applies(options.Profiles, context.Session.Settings.Profile))
            return Task.CompletedTask;

        var modes = this.Modes();
        if (modes.Count == 0)
            return Task.CompletedTask;

        var modeList = String.Join(", ", modes.Select(Name));
        var schema =
            $$"""
            {
              "type": "object",
              "properties": {
                "purpose": { "type": "string", "description": "Shown to the user as the camera title, e.g. 'Point at the nutrition label'." },
                "mode": { "type": "string", "enum": [{{String.Join(", ", modes.Select(m => $"\"{Name(m)}\""))}}], "description": "Default photo." },
                "facing": { "type": "string", "enum": ["back", "front"], "description": "Default back." }
              },
              "required": ["purpose"]
            }
            """;

        context.AddTool(new SchemaFunction(
            ToolName,
            $"Asks the user to point the camera at something you need to see. Modes: {modeList}. " +
            "Only use it when seeing something would really help; the user may decline.",
            schema,
            (AIFunctionArguments args, CancellationToken ct) => this.Use(context, args, ct)
        ));
        return Task.CompletedTask;
    }


    async Task<object?> Use(TurnContext context, AIFunctionArguments args, CancellationToken ct)
    {
        var purpose = args.GetString("purpose");
        if (String.IsNullOrWhiteSpace(purpose))
            return "Error: purpose is required";

        var mode = Parse(args.GetString("mode")) ?? CameraMode.Photo;
        if (!this.Modes().Contains(mode))
            return $"Error: {Name(mode)} isn't available; use one of {String.Join(", ", this.Modes().Select(Name))}";

        var facing = args.GetString("facing")?.Equals("front", StringComparison.OrdinalIgnoreCase) == true ? CameraFacing.Front : CameraFacing.Back;
        var request = new CameraRequest(purpose.Trim(), mode, facing, options.MaxDimension);

        if (options.RequireConfirmation && consent != null && !await consent.Confirm(request, ct).ConfigureAwait(false))
            return "The user declined to open the camera.";

        CameraCapture? result;
        await this.SetFocus(pause: true).ConfigureAwait(false);
        try
        {
            result = await capture.Capture(request, ct).ConfigureAwait(false);
        }
        finally
        {
            await this.SetFocus(pause: false).ConfigureAwait(false);
        }

        if (result == null)
            return "The user cancelled the camera.";

        if (mode != CameraMode.Photo)
            return String.IsNullOrWhiteSpace(result.Text) ? "Nothing was found." : $"{Name(mode)} result:\n{result.Text}";

        if (result.Image is not { } image || image.Length == 0)
            return "No photo was taken.";

        var mediaType = result.MediaType ?? "image/jpeg";
        // stored history only keeps a "[image/jpeg attachment]" placeholder for this: binary never goes into messages
        if (chatClientProvider.SupportsImagesInToolResults)
            return new AIContent[] { new TextContent($"Photo for: {request.Purpose}"), new DataContent(image, mediaType) };

        // most providers can't take an image in a tool result: end this round and send the photo as the
        // user's next message, within the same turn
        context.QueueFollowUp(
            AiInput.Text($"Here is the photo you asked for ({request.Purpose}).", MessageSource.Camera)
                .WithImage(image, mediaType, request.Purpose, options.ImageRetention)
        );
        if (FunctionInvokingChatClient.CurrentContext is { } invocation)
            invocation.Terminate = true;

        return "The photo will arrive in the next message.";
    }


    IReadOnlyList<CameraMode> Modes()
        => capture.SupportedModes.Where(m => options.AllowedModes == null || options.AllowedModes.Contains(m)).ToList();


    async Task SetFocus(bool pause)
    {
        foreach (var participant in this.focus)
        {
            try
            {
                await (pause ? participant.Pause() : participant.Resume()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.log.LogWarning(ex, "Audio focus {Action} failed", pause ? "pause" : "resume");
            }
        }
    }


    static string Name(CameraMode mode) => mode.ToString().ToLowerInvariant();

    static CameraMode? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "photo" => CameraMode.Photo,
        "text" => CameraMode.Text,
        "barcode" => CameraMode.Barcode,
        "document" => CameraMode.Document,
        _ => null
    };
}
