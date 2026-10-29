namespace Vesta;

/// <summary>
/// Names of optional relay capabilities advertised in <see cref="WelcomeMessage.Features"/>.
/// A relay that predates the field sends no list at all; clients treat that as "none".
/// </summary>
public static class RelayFeatures
{
    /// <summary>
    /// The relay understands <see cref="CreateChannelMessage.Encrypted"/>, enforces the
    /// <see cref="EncryptedPayload"/> envelope on encrypted channels and supports
    /// <see cref="RevokeAccessMessage"/>. Clients must not create an encrypted channel on a
    /// relay that does not advertise this: an older relay would ignore the flag and store plaintext.
    /// </summary>
    public const string EncryptedChannels = "encrypted-channels";
}
