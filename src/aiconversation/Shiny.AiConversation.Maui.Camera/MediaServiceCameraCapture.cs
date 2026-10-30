using Shiny.Maui.Controls.Camera.Media;
using CameraViewFacing = Shiny.Controls.Camera.CameraFacing;

namespace Shiny.AiConversation.Maui.Camera;

/// <summary>
/// A scan the app wires up from an analyzer package (e.g. <c>media.ScanBarcodeAsync()</c>); returns the text to give
/// the model, or null when nothing was scanned.
/// </summary>
public delegate Task<string?> CameraScanner(IMediaService media, CameraRequest request, CancellationToken ct);


/// <summary>
/// <see cref="ICameraCapture"/> over Shiny's <see cref="IMediaService"/>: photos come from the CameraView modal
/// (titled with the purpose); scan modes use the scanners registered with <c>AddScanner</c>.
/// </summary>
public class MediaServiceCameraCapture(IMediaService media, IReadOnlyDictionary<CameraMode, CameraScanner> scanners) : ICameraCapture
{
    public IReadOnlyList<CameraMode> SupportedModes => [CameraMode.Photo, .. scanners.Keys.Where(m => m != CameraMode.Photo)];

    public async Task<CameraCapture?> Capture(CameraRequest request, CancellationToken ct)
    {
        if (request.Mode != CameraMode.Photo)
        {
            if (!scanners.TryGetValue(request.Mode, out var scan))
                return null;

            var text = await MainThread.InvokeOnMainThreadAsync(() => scan(media, request, ct)).ConfigureAwait(false);
            return text == null ? null : new CameraCapture(Text: text);
        }

        var photo = await MainThread.InvokeOnMainThreadAsync(() => media.TakePhotoAsync(new PhotoCaptureOptions
        {
            Title = request.Purpose,
            Facing = request.Facing == CameraFacing.Front ? CameraViewFacing.Front : CameraViewFacing.Back,
            MaxDimension = request.MaxDimension,
            PreserveMetadata = false   // never send location/EXIF to the model
        }, ct)).ConfigureAwait(false);

        return photo == null ? null : new CameraCapture(photo.Data, photo.ContentType);
    }
}


/// <summary>
/// Asks with a simple alert on the current page.
/// </summary>
public class AlertCameraConsent : ICameraConsent
{
    public string Title { get; set; } = "Use the camera?";
    public string AllowText { get; set; } = "Open Camera";
    public string DenyText { get; set; } = "Not Now";

    public Task<bool> Confirm(CameraRequest request, CancellationToken ct) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null)
            return false;

        return await page.DisplayAlertAsync(this.Title, $"The assistant would like to use the camera: {request.Purpose}", this.AllowText, this.DenyText);
    });
}
