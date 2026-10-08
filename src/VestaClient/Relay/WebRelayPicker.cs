using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Vesta;

/// <summary>
/// The built-in relay-recovery UI: a loopback-only web page that opens in the user's browser when a
/// connection has exhausted every relay. It is an integral part of <see cref="VestaConnection"/>, not
/// something an app wires up. The page is served from <c>127.0.0.1</c> on a random port under a secret
/// per-launch token, carries no scripts, and drives a <see cref="RelayRecoverySession"/>; it never
/// adopts a relay without an explicit form submit.
/// </summary>
internal sealed class WebRelayPicker : IDisposable
{
    private const int MaxBodyBytes = 8 * 1024;

    private readonly IRelayRecoveryHost _host;
    private readonly VestaAppConfig _config;
    private readonly Func<Uri, bool> _launch;
    private readonly RelayRecoverySession _session;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private HttpListener? _listener;
    private Task? _loop;
    private bool _launchedForOutage;
    private int _disposed;

    public WebRelayPicker(IRelayRecoveryHost host, VestaAppConfig config, Func<Uri, bool>? launch = null, RelayRecoverySession? session = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(config);

        _host = host;
        _config = config;
        _launch = launch ?? OpenSystemBrowser;
        _session = session ?? new RelayRecoverySession(host);
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _session.OnChanged += HandleChanged;
    }

    public RelayRecoverySession Session => _session;

    /// <summary>The page's address once the server has started, otherwise null.</summary>
    public Uri? Url { get; private set; }

    /// <summary>
    /// Show the prompt for an exhausted outage: start the server if needed, open the browser once per
    /// outage, and begin discovering relays. Safe to call repeatedly. Returns true if a user can
    /// reach the page (browser opened, or already open for this outage).
    /// </summary>
    public bool Show(RelaysExhaustedInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        bool launch;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            EnsureStarted();
            launch = !_launchedForOutage;
            _launchedForOutage = true;
        }

        if (!launch)
        {
            return true;
        }

        _session.ReportExhausted(info);
        _ = DiscoverQuietlyAsync();
        return _launch(Url!);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.OnChanged -= HandleChanged;
        _cts.Cancel();
        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        _session.Dispose();
        _cts.Dispose();
    }

    private void HandleChanged(RelayRecoverySnapshot snapshot)
    {
        if (snapshot.Phase == RelayRecoveryPhase.Healthy)
        {
            lock (_gate)
            {
                _launchedForOutage = false;
            }
        }
    }

    private async Task DiscoverQuietlyAsync()
    {
        try
        {
            await _session.DiscoverAsync(_cts.Token);
        }
        catch (Exception)
        {
            // Discovery is best effort; the page still offers manual entry.
        }
    }

    private void EnsureStarted()
    {
        if (_listener is not null)
        {
            return;
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            int port = GetFreePort();
            HttpListener listener = new();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/{_token}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }

            _listener = listener;
            Url = new Uri($"http://127.0.0.1:{port}/{_token}/");
            _loop = Task.Run(() => RunAsync(listener));
            return;
        }

        throw new InvalidOperationException("Could not start the relay picker on a loopback port.");
    }

    private static int GetFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task RunAsync(HttpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                await HandleAsync(context);
            }
            catch (Exception)
            {
                TryRespond(context, HttpStatusCode.InternalServerError, "text/plain", "Error");
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        string basePath = $"/{_token}";

        // The Host header must be our own loopback address (defends against DNS rebinding).
        string expectedHost = $"127.0.0.1:{request.LocalEndPoint.Port}";
        if (!string.Equals(request.Headers["Host"], expectedHost, StringComparison.OrdinalIgnoreCase))
        {
            TryRespond(context, HttpStatusCode.BadRequest, "text/plain", "Bad host");
            return;
        }

        string path = request.Url?.AbsolutePath ?? string.Empty;
        if (!path.StartsWith(basePath + "/", StringComparison.Ordinal))
        {
            TryRespond(context, HttpStatusCode.NotFound, "text/plain", "Not found");
            return;
        }

        string action = path[(basePath.Length + 1)..].TrimEnd('/');

        if (request.HttpMethod == "GET" && action.Length == 0)
        {
            RenderPage(context, basePath);
            return;
        }

        if (request.HttpMethod != "POST")
        {
            TryRespond(context, HttpStatusCode.MethodNotAllowed, "text/plain", "Method not allowed");
            return;
        }

        System.Collections.Specialized.NameValueCollection? form = await ReadFormAsync(request);
        if (form is null)
        {
            TryRespond(context, HttpStatusCode.BadRequest, "text/plain", "Bad request");
            return;
        }

        switch (action)
        {
            case "adopt":
                await AdoptAsync(form);
                break;
            case "retry":
                await _session.RetryAsync(_cts.Token);
                break;
            case "discover":
                await _session.DiscoverAsync(_cts.Token);
                break;
            case "dismiss":
                _session.Dismiss();
                break;
            case "clear":
                await _session.ClearOverrideAsync(_cts.Token);
                break;
            default:
                TryRespond(context, HttpStatusCode.NotFound, "text/plain", "Not found");
                return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.SeeOther;
        context.Response.RedirectLocation = basePath + "/";
        AddSecurityHeaders(context.Response);
        context.Response.Close();
    }

    private async Task AdoptAsync(System.Collections.Specialized.NameValueCollection form)
    {
        string selected = form["relay"] ?? string.Empty;
        RelayAdoptOptions options = new(form["appId"], form["register"] == "1");

        string url = selected == RelayPickerPage.ManualChoice ? form["manual"] ?? string.Empty : selected;
        await _session.UseManualAsync(url, options, _cts.Token);
    }

    private void RenderPage(HttpListenerContext context, string basePath)
    {
        string html = RelayPickerPage.Render(_session.Snapshot, _config, basePath, _host is VestaConnection connection ? connection.ActiveRelay : null);
        TryRespond(context, HttpStatusCode.OK, "text/html; charset=utf-8", html);
    }

    private static async Task<System.Collections.Specialized.NameValueCollection?> ReadFormAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBodyBytes || request.ContentType?.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) != true)
        {
            return request.ContentLength64 == 0 ? HttpUtility.ParseQueryString(string.Empty) : null;
        }

        byte[] buffer = new byte[MaxBodyBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await request.InputStream.ReadAsync(buffer.AsMemory(total));
            if (read == 0)
            {
                break;
            }
            total += read;
        }

        return total > MaxBodyBytes ? null : HttpUtility.ParseQueryString(Encoding.UTF8.GetString(buffer, 0, total));
    }

    private static void AddSecurityHeaders(HttpListenerResponse response)
    {
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Frame-Options"] = "DENY";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'";
    }

    private static void TryRespond(HttpListenerContext context, HttpStatusCode status, string contentType, string body)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            AddSecurityHeaders(context.Response);
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
        }
        catch (Exception)
        {
            // The client went away.
        }
    }

    private static bool OpenSystemBrowser(Uri url)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            return process is not null || OperatingSystem.IsWindows();
        }
        catch (Exception)
        {
            // Headless machine or no registered browser: print the address for whoever is watching.
            Console.Error.WriteLine($"Vesta could not reach any relay. Open {url} in a browser to choose another one.");
            return false;
        }
    }
}
