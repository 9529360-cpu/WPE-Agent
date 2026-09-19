using 币安量化机器人.Services.Access;

namespace 币安量化机器人;

public partial class App
{
    static App()
    {
        var args=Environment.GetCommandLineArgs();
        if(!PrivilegedCommandLineStartupGuard.RequiresLocalSetupAccess(args))return;

        var access=PrivilegedLocalAccessPolicy.Evaluate();
        if(access.Allowed)return;

        Console.Error.WriteLine($"WPE privileged command denied: {access.Code}");
        Environment.Exit(PrivilegedCommandLineStartupGuard.DeniedExitCode);
    }
}

public static class PrivilegedCommandLineStartupGuard
{
    public const int DeniedExitCode=41;
    private static readonly HashSet<string> ProtectedFlags=new(StringComparer.OrdinalIgnoreCase)
    {
        "--provider-readonly-access",
        "--testnet-state-diagnostic",
        "--access-live-test",
        "--four-pillars-live-test",
        "--smoke-test",
        "--risk-aggregate-acceptance",
        "--execution-aggregate-acceptance",
        "--recovery-aggregate-acceptance"
    };

    public static bool RequiresLocalSetupAccess(IEnumerable<string> args)
        =>args.Any(ProtectedFlags.Contains);
}
