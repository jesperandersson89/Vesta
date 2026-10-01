using System.Net;
using System.Text;

namespace VestaClient.Relay;

/// <summary>Renders a <see cref="RelayRecoverySnapshot"/> as the self-contained relay-picker web page (no scripts, no external assets).</summary>
internal static class RelayPickerPage
{
    public const string ManualChoice = "manual";

    public static string Render(RelayRecoverySnapshot snapshot, VestaAppConfig config, string basePath, Uri? activeRelay)
    {
        StringBuilder html = new();
        bool transient = snapshot.Phase is RelayRecoveryPhase.Discovering or RelayRecoveryPhase.Adopting;

        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>Vesta - relay unavailable</title>");
        if (transient)
        {
            html.Append("<meta http-equiv=\"refresh\" content=\"2\">");
        }
        html.Append("<style>");
        html.Append("body{font:15px/1.5 system-ui,sans-serif;max-width:42rem;margin:2rem auto;padding:0 1rem;color:#1d1d1f;background:#fafafa}");
        html.Append("h1{font-size:1.4rem;margin-bottom:.25rem}h2{font-size:1.05rem;margin-top:2rem}");
        html.Append(".muted{color:#6b6b70}.alert{padding:.6rem .8rem;border-radius:6px;margin:1rem 0}");
        html.Append(".err{background:#fdecea;border:1px solid #f5c2bd}.ok{background:#e8f5e9;border:1px solid #b7dfb9}.warn{background:#fff6e0;border:1px solid #f0d99b}");
        html.Append(".relay{display:block;border:1px solid #d6d6db;border-radius:6px;padding:.6rem .8rem;margin:.4rem 0;background:#fff}");
        html.Append(".badge{display:inline-block;font-size:.75rem;border-radius:999px;padding:0 .5rem;margin-left:.3rem;background:#ececf0}");
        html.Append(".badge.good{background:#dff3e1}.badge.open{background:#dbeafe}.badge.bad{background:#fde7d7}");
        html.Append("input[type=text]{width:100%;box-sizing:border-box;padding:.4rem;margin:.25rem 0}");
        html.Append("button{font:inherit;padding:.45rem .9rem;margin:.25rem .25rem .25rem 0;cursor:pointer}");
        html.Append("code{background:#ececf0;padding:0 .25rem;border-radius:3px}ul{padding-left:1.2rem}");
        html.Append("</style></head><body>");

        html.Append("<p class=\"muted\">Vesta &middot; app <code>").Append(E(config.AppId)).Append("</code></p>");

        switch (snapshot.Phase)
        {
            case RelayRecoveryPhase.Healthy:
                RenderConnected(html, snapshot, basePath, activeRelay);
                break;
            case RelayRecoveryPhase.Discovering:
                html.Append("<h1>Looking for relays&hellip;</h1><p class=\"muted\">Asking known relays which servers host this app.</p>");
                break;
            case RelayRecoveryPhase.Adopting:
                html.Append("<h1>Connecting&hellip;</h1><p class=\"muted\">Trying <code>").Append(E(snapshot.Adopting?.ToString() ?? "the relay"))
                    .Append("</code>. The choice is only kept once the relay accepts the app.</p>");
                break;
            default:
                RenderPrompt(html, snapshot, config, basePath);
                break;
        }

        html.Append("</body></html>");
        return html.ToString();
    }

    private static void RenderConnected(StringBuilder html, RelayRecoverySnapshot snapshot, string basePath, Uri? activeRelay)
    {
        html.Append("<h1>Connected</h1><div class=\"alert ok\">The app is connected");
        if (activeRelay is not null)
        {
            html.Append(" to <code>").Append(E(activeRelay.ToString())).Append("</code>");
        }
        html.Append(". You can close this tab.</div>");

        if (snapshot.ActiveOverride is not null)
        {
            html.Append("<p class=\"muted\">You are using a relay you chose yourself. The app owner's own relay list is ignored until you switch back.</p>");
            PostButton(html, basePath, "clear", "Switch back to the app's own relays");
        }
    }

    private static void RenderPrompt(StringBuilder html, RelayRecoverySnapshot snapshot, VestaAppConfig config, string basePath)
    {
        bool dismissed = snapshot.Phase == RelayRecoveryPhase.Degraded;
        html.Append(dismissed
            ? "<h1>Connection lost</h1><p class=\"muted\">Retrying in the background. You can also pick another relay below.</p>"
            : "<h1>Can't reach any relay</h1><p class=\"muted\">Every relay this app knows about is unavailable. Vesta keeps retrying in the background.</p>");

        if (snapshot.FailureReason is not null)
        {
            html.Append("<div class=\"alert err\">").Append(E(snapshot.FailureReason)).Append("</div>");
        }

        if (snapshot.TriedRelays.Count > 0)
        {
            html.Append("<h2>Tried</h2><ul>");
            foreach (RelayAttempt attempt in snapshot.TriedRelays)
            {
                html.Append("<li><code>").Append(E(attempt.Relay.ToString())).Append("</code> <span class=\"muted\">").Append(E(attempt.Reason)).Append("</span></li>");
            }
            html.Append("</ul>");
        }

        html.Append("<h2>Use another relay</h2>");
        if (snapshot.Phase == RelayRecoveryPhase.NothingFound)
        {
            html.Append("<p class=\"muted\">No other relays were found. You can still enter one by hand.</p>");
        }

        string appId = snapshot.FailedOptions?.AppId ?? snapshot.ActiveOverride?.AppId ?? config.AppId;
        bool register = snapshot.FailedOptions?.RegisterApp ?? snapshot.ActiveOverride?.RegisterApp ?? false;
        string manual = snapshot.FailedRelay?.ToString() ?? string.Empty;
        bool manualChecked = snapshot.FailedRelay is not null || snapshot.Choices.Count == 0;

        html.Append("<form method=\"post\" action=\"").Append(E(basePath)).Append("/adopt\">");

        for (int i = 0; i < snapshot.Choices.Count; i++)
        {
            RelayChoice choice = snapshot.Choices[i];
            bool isChecked = !manualChecked && i == 0;
            html.Append("<label class=\"relay\"><input type=\"radio\" name=\"relay\" value=\"").Append(E(choice.Url.ToString())).Append('"')
                .Append(isChecked ? " checked" : string.Empty).Append("> <code>").Append(E(choice.Url.ToString())).Append("</code>");
            AppendBadges(html, choice);
            html.Append("</label>");
        }

        html.Append("<label class=\"relay\"><input type=\"radio\" name=\"relay\" value=\"").Append(ManualChoice).Append('"')
            .Append(manualChecked ? " checked" : string.Empty).Append("> Enter a relay address")
            .Append("<input type=\"text\" name=\"manual\" placeholder=\"wss://relay.example.com\" value=\"").Append(E(manual)).Append("\"></label>");

        html.Append("<h2>App settings on that relay</h2>");
        html.Append("<label>App namespace<input type=\"text\" name=\"appId\" value=\"").Append(E(appId)).Append("\"></label>");
        html.Append("<p class=\"muted\">Channels are stored under this namespace on the relay. Keep <code>").Append(E(config.AppId))
            .Append("</code> unless the relay already uses a different name for this app.</p>");
        html.Append("<label><input type=\"checkbox\" name=\"register\" value=\"1\"").Append(register ? " checked" : string.Empty)
            .Append("> Register app on connect</label>");
        html.Append("<p class=\"muted\">Needed when the relay does not accept unregistered apps and has not seen this namespace yet. "
            + "A relay that does not advertise this app may not hold your data; nothing is changed on the original relay.</p>");
        html.Append("<button type=\"submit\">Use this relay</button></form>");

        html.Append("<h2>Other options</h2>");
        PostButton(html, basePath, "discover", "Find relays");
        PostButton(html, basePath, "retry", "Retry now");
        if (snapshot.ActiveOverride is not null)
        {
            PostButton(html, basePath, "clear", "Switch back to the app's own relays");
        }
        if (!dismissed)
        {
            PostButton(html, basePath, "dismiss", "Keep retrying in the background");
        }
    }

    private static void AppendBadges(StringBuilder html, RelayChoice choice)
    {
        html.Append(choice.HostsRequestedApp
            ? "<span class=\"badge good\">hosts this app</span>"
            : "<span class=\"badge bad\">does not advertise this app</span>");

        switch (choice.AcceptsUnregisteredApps)
        {
            case true:
                html.Append("<span class=\"badge open\">Open relay</span>");
                break;
            case false:
                html.Append("<span class=\"badge\">registered apps only</span>");
                break;
        }

        if (choice.FromCache)
        {
            html.Append("<span class=\"badge\">remembered, not confirmed</span>");
        }
    }

    private static void PostButton(StringBuilder html, string basePath, string action, string label)
    {
        html.Append("<form method=\"post\" action=\"").Append(E(basePath)).Append('/').Append(action).Append("\" style=\"display:inline\"><button type=\"submit\">")
            .Append(E(label)).Append("</button></form>");
    }

    private static string E(string value) => WebUtility.HtmlEncode(value);
}
