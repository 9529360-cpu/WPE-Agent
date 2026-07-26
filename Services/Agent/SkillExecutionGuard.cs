using 币安量化机器人.Core.Models;

namespace 币安量化机器人.Services.Agent;

public sealed class SkillExecutionGuard
{
    private readonly IReadOnlyDictionary<string, SkillDescriptor> _skills;

    public SkillExecutionGuard(IEnumerable<SkillDescriptor>? skills = null)
        => _skills = (skills ?? AgentSkillRegistry.Skills).ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    public SkillDescriptor Authorize(string skillName, TradingMode mode)
    {
        if (!_skills.TryGetValue(skillName, out var skill))
            throw new UnauthorizedAccessException($"Skill '{skillName}' is not registered and cannot be executed.");

        var permissions = skill.Permission.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (permissions.Any(x => x.StartsWith("mainnet:", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException($"Skill '{skillName}' requests forbidden mainnet permission.");
        if (permissions.Any(x => x.Equals("testnet:trade", StringComparison.OrdinalIgnoreCase)) && mode != TradingMode.Testnet)
            throw new UnauthorizedAccessException($"Skill '{skillName}' may trade only in Testnet mode.");
        if (permissions.Any(x => x.Contains("withdraw", StringComparison.OrdinalIgnoreCase) || x.Contains("private-key", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException($"Skill '{skillName}' requests a prohibited credential or withdrawal scope.");
        return skill;
    }

    public void AuthorizePluginPermissions(IEnumerable<string> permissions, TradingMode mode)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        foreach (var permission in permissions)
        {
            if (permission.Contains("mainnet", StringComparison.OrdinalIgnoreCase) ||
                permission.Contains("withdraw", StringComparison.OrdinalIgnoreCase) ||
                permission.Contains("private-key", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Plugin permission '{permission}' is prohibited.");
            if (permission.Equals("exchange.testnet.trade", StringComparison.Ordinal) && mode != TradingMode.Testnet)
                throw new UnauthorizedAccessException("Plugin trading permission is restricted to Testnet.");
        }
    }
}
