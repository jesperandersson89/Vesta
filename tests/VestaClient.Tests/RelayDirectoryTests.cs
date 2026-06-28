using VestaClient.Relay;
using VestaCore.Identity;
using VestaCore.Relay;
using VestaCore.Utilities;

namespace VestaClient.Tests;

/// <summary>
/// Tests for <see cref="RelayDirectory"/> — manifest trust, anti-rollback, and candidate resolution.
/// </summary>
public sealed class RelayDirectoryTests : IDisposable
{
    private readonly VestaIdentity _owner = VestaIdentity.Generate();
    private static readonly Uri Default = new("wss://default.example/ws");

    public void Dispose() => _owner.Dispose();

    private VestaAppConfig Config() => new("chess", _owner.PublicKey, [Default]);

    private RelayManifest SignedManifest(int version, params string[] relayUrls)
    {
        RelayManifest manifest = new()
        {
            AppId = "chess",
            Version = version,
            IssuedAt = DateTimeOffset.UtcNow,
            Relays = relayUrls.Select((url, i) => new RelayEndpoint(url, Priority: i)).ToList(),
            OwnerPublicKey = string.Empty
        };
        return ManifestSigner.Sign(manifest, _owner);
    }

    [Fact]
    public void TryApplyManifest_AcceptsValidOwnerSignedManifest()
    {
        RelayDirectory directory = new(Config());
        RelayManifest manifest = SignedManifest(1, "wss://m1.example/ws");

        Assert.True(directory.TryApplyManifest(manifest));
        Assert.Equal(1, directory.CurrentManifest!.Version);
    }

    [Fact]
    public void TryApplyManifest_RejectsManifestSignedByOtherKey()
    {
        using VestaIdentity attacker = VestaIdentity.Generate();
        RelayManifest manifest = ManifestSigner.Sign(new RelayManifest
        {
            AppId = "chess",
            Version = 5,
            IssuedAt = DateTimeOffset.UtcNow,
            Relays = [new RelayEndpoint("wss://evil.example/ws", 0)],
            OwnerPublicKey = string.Empty
        }, attacker);

        RelayDirectory directory = new(Config());

        Assert.False(directory.TryApplyManifest(manifest));
        Assert.Null(directory.CurrentManifest);
    }

    [Fact]
    public void TryApplyManifest_RejectsWrongAppId()
    {
        RelayManifest manifest = ManifestSigner.Sign(new RelayManifest
        {
            AppId = "othergame",
            Version = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            Relays = [new RelayEndpoint("wss://m1.example/ws", 0)],
            OwnerPublicKey = string.Empty
        }, _owner);

        RelayDirectory directory = new(Config());

        Assert.False(directory.TryApplyManifest(manifest));
    }

    [Fact]
    public void TryApplyManifest_RejectsOlderOrEqualVersion()
    {
        RelayDirectory directory = new(Config());
        Assert.True(directory.TryApplyManifest(SignedManifest(3, "wss://m3.example/ws")));

        Assert.False(directory.TryApplyManifest(SignedManifest(3, "wss://m3b.example/ws")));
        Assert.False(directory.TryApplyManifest(SignedManifest(2, "wss://m2.example/ws")));
        Assert.Equal(3, directory.CurrentManifest!.Version);
    }

    [Fact]
    public void TryApplyManifest_AcceptsNewerVersion()
    {
        RelayDirectory directory = new(Config());
        directory.TryApplyManifest(SignedManifest(1, "wss://m1.example/ws"));

        Assert.True(directory.TryApplyManifest(SignedManifest(2, "wss://m2.example/ws")));
        Assert.Equal(2, directory.CurrentManifest!.Version);
    }

    [Fact]
    public void ResolveCandidates_ManifestRelaysRankAboveDefaults()
    {
        RelayDirectory directory = new(Config());
        directory.TryApplyManifest(SignedManifest(1, "wss://m1.example/ws", "wss://m2.example/ws"));

        IReadOnlyList<Uri> candidates = directory.ResolveCandidates();

        Assert.Equal(new Uri("wss://m1.example/ws"), candidates[0]);
        Assert.Equal(new Uri("wss://m2.example/ws"), candidates[1]);
        Assert.Equal(Default, candidates[^1]);
    }

    [Fact]
    public void ResolveCandidates_NoManifest_ReturnsDefaults()
    {
        RelayDirectory directory = new(Config());

        Assert.Equal([Default], directory.ResolveCandidates());
    }

    [Fact]
    public void ResolveCandidates_RespectsEscapeFallbackValidFrom()
    {
        RelayManifest manifest = ManifestSigner.Sign(new RelayManifest
        {
            AppId = "chess",
            Version = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            Relays = [new RelayEndpoint("wss://m1.example/ws", 0)],
            EscapeFallbacks =
            [
                new EscapeFallback("wss://future.example/ws", DateTimeOffset.UtcNow.AddDays(1)),
                new EscapeFallback("wss://active.example/ws", DateTimeOffset.UtcNow.AddDays(-1))
            ],
            OwnerPublicKey = string.Empty
        }, _owner);

        RelayDirectory directory = new(Config());
        directory.TryApplyManifest(manifest);

        IReadOnlyList<Uri> candidates = directory.ResolveCandidates();

        Assert.Contains(new Uri("wss://active.example/ws"), candidates);
        Assert.DoesNotContain(new Uri("wss://future.example/ws"), candidates);
    }
}
