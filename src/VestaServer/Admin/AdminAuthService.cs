using System.Collections.Concurrent;
using System.Security.Cryptography;
using VestaCore.Identity;
using VestaCore.Utilities;
using VestaServer.Storage;

namespace VestaServer.Admin;

/// <summary>Configuration for the admin HTTP API.</summary>
public sealed class AdminApiOptions
{
  /// <summary>How long a freshly issued challenge nonce remains valid. Default 60 s.</summary>
  public TimeSpan ChallengeTtl { get; set; } = TimeSpan.FromSeconds(60);

  /// <summary>How long a verified bearer token remains valid. Default 1 h.</summary>
  public TimeSpan TokenTtl { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Stateful challenge/token store for the admin HTTP API.
/// Flow:
/// <list type="number">
///   <item><c>POST /admin/auth/challenge</c> \u2192 issues a random nonce (cached for <see cref="AdminApiOptions.ChallengeTtl"/>).</item>
///   <item>Caller signs the nonce bytes with their Ed25519 private key.</item>
///   <item><c>POST /admin/auth/verify { publicKey, nonce, signature }</c> \u2192 verifies the signature, checks the public key against <see cref="IAdminStore"/>, issues a bearer token (cached for <see cref="AdminApiOptions.TokenTtl"/>).</item>
///   <item>Subsequent admin requests carry <c>Authorization: Bearer &lt;token&gt;</c>.</item>
/// </list>
/// Tokens are kept in-process \u2014 a server restart invalidates everything,
/// which matches the bootstrap nature of the admin allow-list. Multi-host
/// deployments would need a shared backend (tracked under TODO #15).
/// </summary>
public sealed class AdminAuthService(IAdminStore adminStore, Microsoft.Extensions.Options.IOptions<AdminApiOptions> options)
{
  private readonly AdminApiOptions _options = options.Value;

  // nonce (base64url) \u2192 expiresAt
  private readonly ConcurrentDictionary<string, DateTimeOffset> _challenges = new();

  // token (base64url) \u2192 (publicKeyHex, expiresAt)
  private readonly ConcurrentDictionary<string, AdminTokenInfo> _tokens = new();

  /// <summary>Issues a new challenge nonce; the caller signs the decoded bytes.</summary>
  public AdminChallenge IssueChallenge()
  {
    PruneExpired();
    byte[] bytes = RandomNumberGenerator.GetBytes(32);
    string nonce = Base64Url.Encode(bytes);
    DateTimeOffset expiresAt = DateTimeOffset.UtcNow + _options.ChallengeTtl;
    _challenges[nonce] = expiresAt;
    return new AdminChallenge(nonce, expiresAt);
  }

  /// <summary>
  /// Verifies a signed challenge. Returns a fresh token on success, or <c>null</c>
  /// when the nonce is unknown / expired, the signature does not verify, or the
  /// public key is not on the admin allow-list.
  /// </summary>
  public async Task<AdminToken?> VerifyAsync(
      string publicKeyBase64Url,
      string nonce,
      string signatureBase64Url,
      CancellationToken cancellationToken = default)
  {
    if (!_challenges.TryRemove(nonce, out DateTimeOffset expires) || expires < DateTimeOffset.UtcNow)
      return null;

    byte[] publicKey;
    byte[] nonceBytes;
    byte[] signature;
    try
    {
      publicKey = Base64Url.Decode(publicKeyBase64Url);
      nonceBytes = Base64Url.Decode(nonce);
      signature = Base64Url.Decode(signatureBase64Url);
    }
    catch (FormatException)
    {
      return null;
    }

    if (publicKey.Length != 32)
      return null;

    if (!VestaIdentity.VerifyWithPublicKey(publicKey, nonceBytes, signature))
      return null;

    if (!await adminStore.IsAdminAsync(publicKey, cancellationToken))
      return null;

    byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
    string token = Base64Url.Encode(tokenBytes);
    DateTimeOffset tokenExpires = DateTimeOffset.UtcNow + _options.TokenTtl;
    _tokens[token] = new AdminTokenInfo(Convert.ToHexString(publicKey), tokenExpires);
    return new AdminToken(token, tokenExpires);
  }

  /// <summary>
  /// Validates a bearer token. Returns the admin's public key (hex) on success
  /// so callers can audit-log who performed an action.
  /// </summary>
  public string? ValidateToken(string? token)
  {
    if (string.IsNullOrEmpty(token)) return null;
    if (!_tokens.TryGetValue(token, out AdminTokenInfo? info)) return null;
    if (info.ExpiresAt < DateTimeOffset.UtcNow)
    {
      _tokens.TryRemove(token, out _);
      return null;
    }
    return info.PublicKeyHex;
  }

  private void PruneExpired()
  {
    DateTimeOffset now = DateTimeOffset.UtcNow;
    foreach (KeyValuePair<string, DateTimeOffset> kv in _challenges)
      if (kv.Value < now)
        _challenges.TryRemove(kv.Key, out _);
    foreach (KeyValuePair<string, AdminTokenInfo> kv in _tokens)
      if (kv.Value.ExpiresAt < now)
        _tokens.TryRemove(kv.Key, out _);
  }

  private sealed record AdminTokenInfo(string PublicKeyHex, DateTimeOffset ExpiresAt);
}

public sealed record AdminChallenge(string Nonce, DateTimeOffset ExpiresAt);
public sealed record AdminToken(string Token, DateTimeOffset ExpiresAt);
