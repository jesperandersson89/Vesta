using System.Text.RegularExpressions;
using AccountServer;
using Microsoft.AspNetCore.Mvc;

// A reference account server for a Vesta game: "log in with a username and password on any device".
// It stores an encrypted identity it cannot read and verifies a login secret it cannot reverse; see README.md.

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new AccountStore(builder.Configuration["Accounts:DataFile"]));
builder.Services.AddSingleton<LoginThrottle>();

WebApplication app = builder.Build();

app.MapPost("/accounts", (SignupRequest request, AccountStore store) =>
{
    if (Validate(request.Username, request.AuthSecret, request.Blob) is IResult invalid)
        return invalid;

    string username = AccountStore.NormalizeUsername(request.Username);
    return store.TryCreate(username, request.AuthSecret, request.Blob)
        ? Results.StatusCode(StatusCodes.Status201Created)
        : Results.Conflict(new { error = "username taken" });
});

app.MapPost("/accounts/login", (LoginRequest request, HttpContext http, AccountStore store, LoginThrottle throttle) =>
{
    IResult? failure = Authenticate(request.Username, request.AuthSecret, http, store, throttle, out Account? account);
    return failure ?? Results.Ok(new { blob = account!.Blob });
});

app.MapPut("/accounts/blob", (UpdateBlobRequest request, HttpContext http, AccountStore store, LoginThrottle throttle) =>
{
    if (Validate(request.Username, request.AuthSecret, request.Blob) is IResult invalid)
        return invalid;
    IResult? failure = Authenticate(request.Username, request.AuthSecret, http, store, throttle, out _);
    if (failure is not null)
        return failure;

    store.Update(AccountStore.NormalizeUsername(request.Username), newAuthSecret: null, request.Blob);
    return Results.NoContent();
});

app.MapPost("/accounts/password", (ChangePasswordRequest request, HttpContext http, AccountStore store, LoginThrottle throttle) =>
{
    if (Validate(request.Username, request.NewAuthSecret, request.Blob) is IResult invalid)
        return invalid;
    IResult? failure = Authenticate(request.Username, request.AuthSecret, http, store, throttle, out _);
    if (failure is not null)
        return failure;

    store.Update(AccountStore.NormalizeUsername(request.Username), request.NewAuthSecret, request.Blob);
    return Results.NoContent();
});

app.MapDelete("/accounts", ([FromBody] LoginRequest request, HttpContext http, AccountStore store, LoginThrottle throttle) =>
{
    IResult? failure = Authenticate(request.Username, request.AuthSecret, http, store, throttle, out _);
    if (failure is not null)
        return failure;

    store.Delete(AccountStore.NormalizeUsername(request.Username));
    return Results.NoContent();
});

app.Run();

static IResult? Validate(string? username, string? authSecret, string? blob)
{
    string normalized = AccountStore.NormalizeUsername(username ?? string.Empty);
    if (normalized.Length is < 3 or > 64)
        return Results.BadRequest(new { error = "username must be 3-64 characters" });
    if (authSecret is null || !AuthSecretFormat().IsMatch(authSecret))
        return Results.BadRequest(new { error = "authSecret must be 43 base64url characters" });
    if (blob is null || blob.Length is 0 or > 4096)
        return Results.BadRequest(new { error = "blob must be 1-4096 characters" });
    return null;
}

// Unknown user and wrong secret are indistinguishable, and both count towards the throttle.
static IResult? Authenticate(
    string? username,
    string? authSecret,
    HttpContext http,
    AccountStore store,
    LoginThrottle throttle,
    out Account? account)
{
    account = null;
    string normalized = AccountStore.NormalizeUsername(username ?? string.Empty);
    if (authSecret is null || !AuthSecretFormat().IsMatch(authSecret))
        return Results.BadRequest(new { error = "authSecret must be 43 base64url characters" });

    string userKey = "u:" + normalized;
    string addressKey = "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    if (throttle.IsBlocked(userKey, LoginThrottle.UserLimit) || throttle.IsBlocked(addressKey, LoginThrottle.AddressLimit))
        return Results.Json(new { error = "too many failed attempts" }, statusCode: StatusCodes.Status429TooManyRequests);

    account = store.Verify(normalized, authSecret);
    if (account is null)
    {
        throttle.RecordFailure(userKey);
        throttle.RecordFailure(addressKey);
        return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    throttle.Reset(userKey);
    return null;
}

internal sealed record SignupRequest(string Username, string AuthSecret, string Blob);

internal sealed record LoginRequest(string Username, string AuthSecret);

internal sealed record UpdateBlobRequest(string Username, string AuthSecret, string Blob);

internal sealed record ChangePasswordRequest(string Username, string AuthSecret, string NewAuthSecret, string Blob);

// Make the implicit Program class accessible to WebApplicationFactory<Program> in tests.
public partial class Program
{
    [GeneratedRegex("^[A-Za-z0-9_-]{43}$")]
    private static partial Regex AuthSecretFormat();
}
