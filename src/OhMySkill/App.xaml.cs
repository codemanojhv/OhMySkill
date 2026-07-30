using System.Windows;

namespace OhMySkill;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(a => string.Equals(a, "--self-check", StringComparison.OrdinalIgnoreCase)))
        {
            var result = SelfCheck.Run();
            Shutdown(result ? 0 : 1);
        }
    }
}
