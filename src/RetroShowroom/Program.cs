using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RetroShowroom.Core;

namespace RetroShowroom;

/// <summary>
/// Eigener Einstiegspunkt statt des von XAML erzeugten (siehe DISABLE_XAML_GENERATED_MAIN in der Projektdatei).
/// Mit <c>--update-anwenden</c> läuft die .exe nur als Installer: ohne Oberfläche, sie ersetzt die alte .exe
/// und beendet sich wieder (siehe <see cref="UpdateInstaller"/>).
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (UpdateInstaller.TryParseArgs(args, out var job))
            return job is null ? 2 : UpdateInstaller.Apply(job);

        // Ab hier wie der erzeugte Einstiegspunkt
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
