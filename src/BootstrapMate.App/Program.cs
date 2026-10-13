using BootstrapMate.Core;

namespace BootstrapMate.App;

/// <summary>
/// Entry point. Replaces the XAML-generated Main (DISABLE_XAML_GENERATED_MAIN) so the elevated
/// settings writer (--apply-settings &lt;pipe&gt;) runs headless and exits before any window exists.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (PrefsSettingsWriter.IsApplyRequest(args))
        {
            return PrefsSettingsWriter.ApplyPipeName(args) is { } pipeName
                ? PrefsSettingsWriter.Apply(pipeName)
                : PrefsSettingsWriter.ExitUsage;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
