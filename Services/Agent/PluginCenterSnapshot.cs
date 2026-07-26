using System.Text;
using WpeAgent.Plugins;

namespace 币安量化机器人.Services.Agent;

public static class PluginCenterSnapshot
{
    public static string Build(AgentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var snapshot = LocalPluginRegistry.CreateDefault().List();
        var text = new StringBuilder();
        text.AppendLine("WPE PLUGIN PHASE 0 - READ ONLY");
        text.AppendLine($"State: {snapshot.State}");
        if (!string.IsNullOrWhiteSpace(snapshot.Message)) text.AppendLine(snapshot.Message);
        text.AppendLine();
        foreach (var plugin in snapshot.Items)
        {
            text.AppendLine($"{(plugin.Enabled ? "ENABLED" : "DISABLED")}  {plugin.Manifest.Name} [{plugin.Manifest.Id}]");
            text.AppendLine($"  type={plugin.Manifest.Type} version={plugin.Manifest.Version} compatibility={plugin.CompatibilityStatus}");
            text.AppendLine($"  permissions={string.Join(",", plugin.Manifest.Permissions)} risk={plugin.RiskLevel} testnetOnly={plugin.Manifest.Lifecycle.TestnetOnly}");
        }
        text.AppendLine();
        text.AppendLine("Manifests are catalogued only. WPE does not load plugin code or install remote packages in Phase 0.");
        text.AppendLine("Exchange adapters are Testnet-only, disabled by default, and cannot bypass the execution safety chain.");
        return text.ToString();
    }
}
