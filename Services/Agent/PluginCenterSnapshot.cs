using System.Text;
using 币安量化机器人.Services.Exchange;

namespace 币安量化机器人.Services.Agent;

public static class PluginCenterSnapshot
{
    public static string Build(AgentSettings settings)
    {
        var catalog = new ExchangeProviderCatalog();
        var text = new StringBuilder();
        text.AppendLine("LLM / ASSISTANT ADAPTERS");
        var local = settings.Brains.Values.Any(x => x.IsLocal || x.Provider.Equals("WPE Local Brain", StringComparison.OrdinalIgnoreCase));
        text.AppendLine($"● WPE Local Brain   {(local ? "READY" : "AVAILABLE")}");
        foreach (var brain in settings.Brains.Values.Where(x => !x.IsLocal && !x.Provider.Equals("WPE Local Brain", StringComparison.OrdinalIgnoreCase)))
            text.AppendLine($"○ {brain.Provider} / {brain.Model}   {(string.IsNullOrWhiteSpace(brain.EncryptedKey) ? "NOT CONFIGURED" : "CONFIGURED")}");
        text.AppendLine();
        text.AppendLine("EXCHANGE ADAPTERS");
        foreach (var provider in catalog.All.OrderBy(x => x.Id))
            text.AppendLine($"{(catalog.IsInstalled(provider.Id) ? "●" : "○")} {provider.DisplayName} [{provider.Id}]   {provider.AssetClass}");
        text.AppendLine();
        text.AppendLine($"CORE SKILLS   {AgentSkillRegistry.Skills.Count} registered");
        foreach (var skill in AgentSkillRegistry.Skills.OrderBy(x => x.Name))
            text.AppendLine($"● {skill.Name}  timeout={skill.TimeoutSeconds}s retries={skill.Retries}");
        text.AppendLine();
        text.AppendLine("CONTROL POLICY");
        text.AppendLine("Workflow / State / Scheduler / Risk remain authoritative.");
        text.AppendLine("Assistant adapters have no order or risk permissions.");
        return text.ToString();
    }
}
