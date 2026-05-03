namespace Shiny.Speech;

public class ActivityProvider : Java.Lang.Object, Application.IActivityLifecycleCallbacks
{
    Activity? currentActivity;

    public ActivityProvider()
    {
        var app = Application.Context as Application
            ?? throw new InvalidOperationException("Application.Context is not an Application instance");
        app.RegisterActivityLifecycleCallbacks(this);
    }

    public Activity? Current => currentActivity;

    public void OnActivityResumed(Activity activity) => currentActivity = activity;
    public void OnActivityPaused(Activity activity) { if (currentActivity == activity) currentActivity = null; }
    public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) => currentActivity = activity;
    public void OnActivityStarted(Activity activity) => currentActivity ??= activity;
    public void OnActivityStopped(Activity activity) { }
    public void OnActivitySaveInstanceState(Activity activity, Bundle outState) { }
    public void OnActivityDestroyed(Activity activity) { if (currentActivity == activity) currentActivity = null; }
}
