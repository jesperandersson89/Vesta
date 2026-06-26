// Generates a fresh Ed25519 relay-operator keypair for the managed-cloud bootstrap.
//
// The SAME keypair binds the two stacks:
//   * PUBLIC half  -> Vesta relay  (Admin:BootstrapPublicKeys allow-list)
//   * PRIVATE half -> Atrium portal (Relay:AdminPrivateKey, stored in Key Vault)
//
// Encoding matches VestaIdentity exactly (base64url of the 32-byte seed / public key),
// so the relay and Atrium accept the values verbatim. Nothing is written to disk —
// copy the values straight into your secret store / GitHub secrets.
//
// Run from the repo root (needs the .NET 10 SDK):
//   dotnet run .github/infra/scripts/new-relay-keypair.cs

#:project ../../../src/VestaCore/VestaCore.csproj

using VestaCore.Identity;
using VestaCore.Utilities;

using VestaIdentity identity = VestaIdentity.Generate();

string publicKey = Base64Url.Encode(identity.PublicKey);
string privateKey = Base64Url.Encode(identity.ExportPrivateKey());

Console.WriteLine();
Console.WriteLine("Relay operator keypair (Ed25519) — store these in your secret stores:");
Console.WriteLine();
Console.WriteLine($"  clientId (informational): {identity.ClientId}");
Console.WriteLine();
Console.WriteLine("PUBLIC key  -> Vesta relay stack");
Console.WriteLine($"  GitHub secret  VESTA_RELAY_ADMIN_PUBLICKEY = {publicKey}");
Console.WriteLine($"  bicep param    relayAdminPublicKey          = {publicKey}");
Console.WriteLine();
Console.WriteLine("PRIVATE key -> Atrium portal stack (goes into Key Vault)");
Console.WriteLine($"  GitHub secret  ATRIUM_RELAY_ADMIN_PRIVATEKEY = {privateKey}");
Console.WriteLine($"  bicep param    relayAdminPrivateKey          = {privateKey}");
Console.WriteLine();
Console.WriteLine("Keep the PRIVATE key secret. Never commit it; never hand it to a tenant.");
Console.WriteLine();
