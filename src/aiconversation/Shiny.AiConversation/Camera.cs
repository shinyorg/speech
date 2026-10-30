namespace Shiny.AiConversation;

public enum CameraMode
{
    /// <summary>
    /// A photo, sent to the model as an image.
    /// </summary>
    Photo,

    /// <summary>
    /// Read text (OCR); the text is sent, not the image.
    /// </summary>
    Text,

    Barcode,

    /// <summary>
    /// A structured document (receipt, business card, ID…); its fields are sent as text.
    /// </summary>
    Document
}

public enum CameraFacing
{
    Back,
    Front
}

/// <summary>
/// Settings for the <c>use_camera</c> tool (D14).
/// </summary>
public record CameraToolOptions
{
    /// <summary>
    /// Ask the user before the camera opens. The camera page also always shows the purpose as its title.
    /// </summary>
    public bool RequireConfirmation { get; init; } = true;

    public ImageRetention ImageRetention { get; init; } = ImageRetention.DropAfterTurn;

    /// <summary>
    /// Longest edge of photos sent to the model (smaller is cheaper; 1568 suits most vision models).
    /// </summary>
    public int MaxDimension { get; init; } = 1568;

    /// <summary>
    /// Modes the model may ask for; null means every mode the capture service supports.
    /// </summary>
    public IReadOnlyList<CameraMode>? AllowedModes { get; init; }

    /// <summary>
    /// Limit the tool to sessions with one of these profiles; empty means every session.
    /// </summary>
    public IReadOnlyList<string> Profiles { get; init; } = [];
}

public record CameraRequest(string Purpose, CameraMode Mode, CameraFacing Facing, int MaxDimension);

/// <summary>
/// What the camera produced: an image (photo mode) or text (scan modes).
/// </summary>
public record CameraCapture(ReadOnlyMemory<byte>? Image = null, string? MediaType = null, string? Text = null);

/// <summary>
/// Opens the camera. The MAUI implementation presents Shiny's CameraView modal.
/// </summary>
public interface ICameraCapture
{
    IReadOnlyList<CameraMode> SupportedModes { get; }

    /// <summary>
    /// Returns null when the user cancels or permission is refused.
    /// </summary>
    Task<CameraCapture?> Capture(CameraRequest request, CancellationToken ct);
}

/// <summary>
/// Asks the user whether the assistant may open the camera.
/// </summary>
public interface ICameraConsent
{
    Task<bool> Confirm(CameraRequest request, CancellationToken ct);
}
