using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shiny.AiConversation;
using Shiny.AiConversation.Infrastructure;
using Shiny.AiConversation.Maui.Camera;
using Shiny.Maui.Controls.Camera.Media;

namespace Shiny;

public class AiConversationCameraOptions(IServiceCollection services)
{
    internal Dictionary<CameraMode, CameraScanner> Scanners { get; } = new();
    internal CameraToolOptions Options { get; private set; } = new();

    public IServiceCollection Services => services;

    public AiConversationCameraOptions Configure(Func<CameraToolOptions, CameraToolOptions> configure)
    {
        this.Options = configure(this.Options);
        return this;
    }

    /// <summary>
    /// Enables a scan mode with a scan from an analyzer package, e.g.
    /// <c>AddScanner(CameraMode.Barcode, async (media, req, ct) => (await media.ScanBarcodeAsync())?.Value)</c>.
    /// </summary>
    public AiConversationCameraOptions AddScanner(CameraMode mode, CameraScanner scanner)
    {
        this.Scanners[mode] = scanner;
        return this;
    }

    /// <summary>Replaces the consent prompt (an alert by default).</summary>
    public AiConversationCameraOptions SetConsent<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TConsent>() where TConsent : class, ICameraConsent
    {
        services.TryAddSingleton<ICameraConsent, TConsent>();
        return this;
    }
}


public static class AiConversationCameraRegistrationExtensions
{
    /// <summary>
    /// Adds the <c>use_camera</c> tool. Needs <c>UseShinyCamera()</c> on the MAUI app builder (it registers <see cref="IMediaService"/>).
    /// </summary>
    public static IServiceCollection AddAiConversationCamera(this IServiceCollection services, Action<AiConversationCameraOptions>? configure = null)
    {
        var options = new AiConversationCameraOptions(services);
        configure?.Invoke(options);

        services.TryAddSingleton<ICameraConsent, AlertCameraConsent>();
        services.AddSingleton<ICameraCapture>(sp => new MediaServiceCameraCapture(sp.GetRequiredService<IMediaService>(), options.Scanners));
        services.AddSingleton<ISessionInterceptor>(sp => new CameraToolInterceptor(
            sp.GetRequiredService<ICameraCapture>(),
            sp.GetRequiredService<IChatClientProvider>(),
            sp.GetServices<IAudioFocusParticipant>(),
            options.Options,
            sp.GetService<ICameraConsent>()
        ));
        return services;
    }
}
