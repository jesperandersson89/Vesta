using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using VestaCore.Utilities;

namespace VestaServer.Storage;

/// <summary>
/// Options controlling who is granted server-admin status. Loaded from the
/// <c>Admin</c> section of <c>appsettings.json</c>.
/// </summary>
public sealed class AdminOptions
{
  /// <summary>
  /// Ed25519 public keys (base64url, 32 bytes decoded) of clients that should
  /// be promoted to admin during HELLO. A client whose <c>HELLO.PublicKey</c>
  /// matches one of these entries can issue admin-only commands such as
  /// <see cref="VestaCore.Protocol.DeleteChannelMessage"/>.
  /// </summary>
  public IList<string> BootstrapPublicKeys { get; set; } = [];
}

/// <summary>
/// Server-side abstraction for deciding whether a connection holds admin
/// privileges. Implementations look up a verified public key against an
/// allow-list. There is no per-channel admin concept here \u2014 this is the
/// global "server operator" role, separate from the per-channel admin role
/// stored in <c>channel_access</c>.
/// </summary>
public interface IAdminStore
{
  /// <summary>
  /// Returns true if the given verified Ed25519 public key belongs to a
  /// server admin. <paramref name="publicKey"/> is the 32-byte decoded
  /// public key as set on <c>ClientConnection.PublicKey</c> after HELLO
  /// validation.
  /// </summary>
  Task<bool> IsAdminAsync(byte[] publicKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IAdminStore"/> backed by <see cref="AdminOptions.BootstrapPublicKeys"/>.
/// Reads the allow-list at construction time and decodes each base64url
/// entry into the raw 32-byte form for constant-time comparison against
/// connection public keys.
/// </summary>
public sealed class ConfigAdminStore : IAdminStore
{
  private readonly ConcurrentDictionary<string, byte> _allowed = new(StringComparer.Ordinal);

  public ConfigAdminStore(IOptions<AdminOptions> options)
  {
    foreach (string entry in options.Value.BootstrapPublicKeys ?? [])
    {
      if (string.IsNullOrWhiteSpace(entry))
        continue;
      try
      {
        byte[] decoded = Base64Url.Decode(entry.Trim());
        if (decoded.Length != 32)
          continue;
        _allowed.TryAdd(Convert.ToHexString(decoded), 0);
      }
      catch (FormatException)
      {
        // Skip malformed entries silently \u2014 they would otherwise crash startup.
        // Operators see them only if they grep the config or hit logs.
      }
    }
  }

  public Task<bool> IsAdminAsync(byte[] publicKey, CancellationToken cancellationToken = default)
  {
    if (publicKey is null || publicKey.Length != 32 || _allowed.IsEmpty)
      return Task.FromResult(false);
    string key = Convert.ToHexString(publicKey);
    return Task.FromResult(_allowed.ContainsKey(key));
  }

  /// <summary>True if any admin keys were configured. Useful for warning logs at startup.</summary>
  public bool HasAdmins => !_allowed.IsEmpty;
}
