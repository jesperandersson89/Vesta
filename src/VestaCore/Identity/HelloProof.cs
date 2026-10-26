using System.Text;

namespace Vesta;

/// <summary>
/// The HELLO key-possession proof: a relay sends a nonce in a <see cref="ChallengeMessage"/> and the
/// client signs it, proving it holds the private key for the <c>clientId</c> it announced.
/// </summary>
public static class HelloProof
{
    /// <summary>Domain separator so the signature can never be mistaken for an event signature.</summary>
    public const string Domain = "vesta-hello-v1";

    /// <summary>The exact bytes that are signed: <c>"vesta-hello-v1\n{nonce}\n{clientId}"</c> in UTF-8.</summary>
    public static byte[] BuildMessage(string nonce, string clientId) =>
        Encoding.UTF8.GetBytes($"{Domain}\n{nonce}\n{clientId}");

    /// <summary>Signs the challenge and returns the base64url signature to put in <see cref="ProveMessage"/>.</summary>
    public static string Sign(VestaIdentity identity, string nonce) =>
        Base64Url.Encode(identity.Sign(BuildMessage(nonce, identity.ClientId)));

    /// <summary>Verifies a base64url signature against the public key the client announced.</summary>
    public static bool Verify(byte[] publicKey, string nonce, string clientId, string signature)
    {
        try
        {
            return VestaIdentity.VerifyWithPublicKey(publicKey, BuildMessage(nonce, clientId), Base64Url.Decode(signature));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
