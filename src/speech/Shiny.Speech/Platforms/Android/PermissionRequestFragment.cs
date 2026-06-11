using Android.Content.PM;
using AndroidX.Fragment.App;
using Fragment = AndroidX.Fragment.App.Fragment;

namespace Shiny.Speech;

class PermissionRequestFragment : Fragment
{
    const int RequestCode = 29471;

    TaskCompletionSource<bool>? tcs;
    string? permission;

    public Task<bool> RequestAsync(FragmentActivity activity, string perm)
    {
        tcs = new TaskCompletionSource<bool>();
        permission = perm;

        activity.SupportFragmentManager
            .BeginTransaction()
            .Add(this, "shiny_speech_perm")
            .CommitAllowingStateLoss();

        activity.SupportFragmentManager.ExecutePendingTransactions();
        this.RequestPermissions([permission], RequestCode);

        return tcs.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (requestCode != RequestCode) return;

        var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
        tcs?.TrySetResult(granted);

        Activity?.SupportFragmentManager
            .BeginTransaction()
            .Remove(this)
            .CommitAllowingStateLoss();
    }
}
