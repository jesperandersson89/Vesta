// Generates a fresh Ed25519 relay-operator keypair.
//
// A relay operator uses ONE keypair to administer a relay over its /admin/* HTTP API:
//   * PUBLIC half  -> the relay             (Admin:BootstrapPublicKeys allow-list)
//   * PRIVATE half -> the operator's tooling (e.g. a control plane that provisions apps)
//
// Only relay operators need this; app developers never do.
//
// Encoding matches VestaIdentity exactly (base64url of the 32-byte seed / public key),
// so the relay accepts the public key verbatim. Nothing is written to disk — copy the
// values straight into your secret store.
//
// Run from the repo root (needs the .NET 10 SDK):
//   dotnet run .github/scripts/new-relay-keypair.cs

#:project ../../src/VestaCore/VestaCore.csproj

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
Console.WriteLine("PUBLIC key  -> the relay");
Console.WriteLine($"  env var  Admin__BootstrapPublicKeys__0 = {publicKey}");
Console.WriteLine();
Console.WriteLine("PRIVATE key -> the operator's admin tooling (keep secret)");
Console.WriteLine($"  {privateKey}");
Console.WriteLine();
Console.WriteLine("Never commit the PRIVATE key; never hand it to a tenant.");
Console.WriteLine();
