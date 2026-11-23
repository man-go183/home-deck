using Android.App;
using Android.Content;
using Android.Service.Notification;

namespace HomeDeck;

/// <summary>
/// Read access to the user's notifications (user's standing requirement).
/// The user enables Home Deck once in system Settings → Notifications →
/// Notification access; the page nudges them there via
/// bridge openNotificationSettings(). Active notifications are served
/// to pages through bridge getNotifications() (poll — no push in v1).
/// Manifest entry is generated from the attributes below (no XML edit).
/// </summary>
[Service(Exported = true, Permission = "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE")]
[IntentFilter(new[] { "android.service.notification.NotificationListenerService" })]
public class HdNotificationService : NotificationListenerService
{
    static readonly object _gate = new();
    static List<Dictionary<string, string?>> _active = new();

    public override void OnNotificationPosted(StatusBarNotification? sbn)
    {
        try { Snapshot(); } catch { }
    }

    public override void OnNotificationRemoved(StatusBarNotification? sbn)
    {
        try { Snapshot(); } catch { }
    }

    public override void OnListenerConnected()
    {
        try { Snapshot(); } catch { }
    }

    void Snapshot()
    {
        var list = new List<Dictionary<string, string?>>();
        try
        {
            var actives = GetActiveNotifications();
            for (int i = 0; i < actives.Length && i < 100; i++)
            {
                var n = actives[i];
                if (n == null || n.Notification == null) continue;
                string title = "", text = "";
                try
                {
                    var ex = n.Notification.Extras;
                    title = ex?.GetCharSequence(Notification.ExtraTitle)?.ToString() ?? "";
                    text = ex?.GetCharSequence(Notification.ExtraText)?.ToString() ?? "";
                }
                catch { }
                list.Add(new Dictionary<string, string?>
                {
                    ["package"] = n.PackageName ?? "",
                    ["title"] = title.Length > 200 ? title[..200] : title,
                    ["text"] = text.Length > 500 ? text[..500] : text,
                    ["time"] = DateTimeOffset.FromUnixTimeMilliseconds(n.PostTime).UtcDateTime.ToString("o"),
                });
            }
        }
        catch { }
        lock (_gate) { _active = list; }
    }

    public static List<Dictionary<string, string?>> Active()
    {
        lock (_gate) { return new List<Dictionary<string, string?>>(_active); }
    }

    /// <summary>True once the user has enabled Home Deck as a
    /// notification listener in system settings.</summary>
    public static bool IsEnabled(Context ctx)
    {
        try
        {
            var flat = Android.Provider.Settings.Secure.GetString(
                ctx.ContentResolver, "enabled_notification_listeners");
            return flat != null && flat.Contains(ctx.PackageName, StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
