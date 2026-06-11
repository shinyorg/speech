using Shiny.Speech;

namespace Shiny.AiConversation.Infrastructure;

public class DefaultSoundPlayer(IAudioPlayer audioPlayer) : ISoundProvider
{
    public async Task Play(AiAction action)
    {
        var resourceName = action switch
        {
            AiAction.Ok => "ok.wav",
            AiAction.Think => "think.wav",
            AiAction.Respond => "responding.wav",
            AiAction.Cancel => "cancel.wav",
            AiAction.Error => "error.wav",
            _ => null
        };

        if (resourceName == null)
            return;

        var assembly = typeof(DefaultSoundPlayer).Assembly;
        var fullResourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith(resourceName));

        if (fullResourceName == null)
            return;

        await using var stream = assembly.GetManifestResourceStream(fullResourceName)!;
        await audioPlayer.PlayAsync(stream);
    }
}