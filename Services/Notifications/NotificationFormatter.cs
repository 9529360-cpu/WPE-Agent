using System.Globalization;

namespace WpeAgent.Notifications;

public sealed class NotificationFormatter
{
    public NotificationOutboundMessage Format(ConfirmedNotificationEvent value)
    {
        if(string.IsNullOrWhiteSpace(value.EventKey))throw new ArgumentException("Notification event key is required.");
        if(value.OccurredAtUtc.Kind!=DateTimeKind.Utc)throw new ArgumentException("Notification event time must be UTC.");
        if(string.IsNullOrWhiteSpace(value.DiagnosticCode))throw new ArgumentException("Notification diagnostic code is required.");

        var fields=new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["environment"]=Clean(value.Environment),
            ["provider"]=Clean(value.Provider),
            ["event"]=EventName(value.Kind),
            ["utc_time"]=value.OccurredAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'",CultureInfo.InvariantCulture),
            ["diagnostic_code"]=Clean(value.DiagnosticCode)
        };
        Add(fields,"symbol",value.Symbol);
        Add(fields,"side",value.Side);
        Add(fields,"filled_price",value.FilledPrice);
        Add(fields,"quantity",value.Quantity);
        Add(fields,"stop_loss",value.StopLoss);
        Add(fields,"take_profit",value.TakeProfit);
        if(!string.IsNullOrWhiteSpace(value.Content))fields["content"]=CleanMultiline(value.Content);

        var text=string.Join(Environment.NewLine,fields.Select(x=>$"{Label(x.Key)}: {x.Value}"));
        return new(value.EventKey,value.Kind,text,fields);
    }

    private static void Add(IDictionary<string,string> fields,string key,string? value)
    {
        if(!string.IsNullOrWhiteSpace(value))fields[key]=Clean(value);
    }
    private static void Add(IDictionary<string,string> fields,string key,decimal? value)
    {
        if(value is not null)fields[key]=value.Value.ToString("0.################",CultureInfo.InvariantCulture);
    }
    private static string Clean(string value)=>value.Replace('\r',' ').Replace('\n',' ').Trim();
    private static string CleanMultiline(string value)=>string.Join('\n',value.Replace("\r\n","\n",StringComparison.Ordinal).Replace('\r','\n').Split('\n').Select(Clean)).Trim();
    private static string EventName(NotificationEventKind value)=>value switch
    {
        NotificationEventKind.OrderFilled=>"Order filled",
        NotificationEventKind.PositionOpened=>"Position opened",
        NotificationEventKind.PositionClosed=>"Position closed",
        NotificationEventKind.ProtectionPlaced=>"Protection placed",
        NotificationEventKind.ProtectionUpdated=>"Protection updated",
        NotificationEventKind.ProtectionFailed=>"Protection failed",
        NotificationEventKind.RiskBlocked=>"Risk blocked",
        NotificationEventKind.AgentDegraded=>"Agent degraded",
        NotificationEventKind.Test=>"Test notification",
        _=>value.ToString()
    };
    private static string Label(string key)=>key.Replace('_',' ');
}
