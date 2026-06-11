using CarPlay;
using Foundation;

namespace Sample;

[Register("CarPlaySceneDelegate")]
public class CarPlaySceneDelegate : CPTemplateApplicationSceneDelegate
{
    CPInterfaceController? interfaceController;
    CarPlayConversationManager? conversationManager;

    public override void DidConnect(CPTemplateApplicationScene templateApplicationScene, CPInterfaceController interfaceController)
    {
        this.interfaceController = interfaceController;
        this.conversationManager = new CarPlayConversationManager(interfaceController);
        this.conversationManager.Show();
    }

    public override void DidDisconnect(CPTemplateApplicationScene templateApplicationScene, CPInterfaceController interfaceController)
    {
        this.conversationManager?.Cleanup();
        this.conversationManager = null;
        this.interfaceController = null;
    }
}
