using 币安量化机器人.Services;

namespace WpeAgent.Headless;

public sealed record HeadlessDataRootResolution(
    bool Success,
    string? Root,
    string Code);

public static class HeadlessDataRootBootstrap
{
    public const string ArgumentName = "--data-root";

    public static HeadlessDataRootResolution Resolve(
        IReadOnlyList<string> args,
        string? environmentRoot)
    {
        string? argumentRoot = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], ArgumentName, StringComparison.Ordinal))
                continue;

            if (argumentRoot is not null)
                return Reject("headless.data-root-duplicate");
            if (index + 1 >= args.Count ||
                string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
                return Reject("headless.data-root-argument-invalid");

            argumentRoot = args[++index];
        }

        var argument = Normalize(argumentRoot);
        if (argumentRoot is not null && argument is null)
            return Reject("headless.data-root-argument-invalid");

        var environment = Normalize(environmentRoot);
        if (!string.IsNullOrWhiteSpace(environmentRoot) && environment is null)
            return Reject("headless.data-root-environment-invalid");

        if (argument is not null &&
            environment is not null &&
            !string.Equals(argument, environment, StringComparison.OrdinalIgnoreCase))
            return Reject("headless.data-root-conflict");

        var root = argument ?? environment;
        return new(
            true,
            root,
            root is null
                ? "headless.data-root-default"
                : "headless.data-root-explicit");
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DataRootPathPolicy.TryNormalizeFixedLocalRoot(value, out var normalized)
            ? normalized
            : null;
    }

    private static HeadlessDataRootResolution Reject(string code)
        => new(false, null, code);
}
