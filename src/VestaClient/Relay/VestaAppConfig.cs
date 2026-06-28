namespace VestaClient.Relay;

/// <summary>
/// The app-level configuration an app compiles in to participate in relay-independent
/// coordination. The <see cref="OwnerPublicKey"/> is the trust anchor: only relay manifests
/// signed by this key are ever accepted, so an owner can move the swarm to a new relay without
/// any relay-side cooperation, and no third party can hijack the app's rendezvous.
/// </summary>
/// <param name="AppId">The app id (first channel segment) this config governs.</param>
/// <param name="OwnerPublicKey">The app owner's Ed25519 public key bytes — the manifest trust anchor.</param>
/// <param name="DefaultRelays">
/// The compiled-in default relays, in preference order. Used to bootstrap the very first
/// connection and as the last-resort fallback if no manifest or override is available.
/// </param>
public sealed record VestaAppConfig(
    string AppId,
    byte[] OwnerPublicKey,
    IReadOnlyList<Uri> DefaultRelays);
