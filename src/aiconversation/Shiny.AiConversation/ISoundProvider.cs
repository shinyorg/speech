namespace Shiny.AiConversation;

public enum AiAction
{
    Ok,
    Cancel,
    Respond,
    Think,
    Error
}
public interface ISoundProvider
{
    Task Play(AiAction state);
}