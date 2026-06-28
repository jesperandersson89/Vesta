using VestaClient.Relay;

namespace VestaClient.Tests;

/// <summary>
/// Tests for relay resolution precedence (<see cref="RelayResolver"/>) and the
/// persisted user override (<see cref="FileRelayOverrideStore"/>).
/// </summary>
public sealed class RelayResolutionTests : IDisposable
{
    private static readonly Uri Default1 = new("wss://relay.example/ws");
    private static readonly Uri Default2 = new("wss://backup.example/ws");
    private static readonly Uri Manifest1 = new("wss://manifest-a.example/ws");
    private static readonly Uri Manifest2 = new("wss://manifest-b.example/ws");
    private static readonly Uri Override = new("wss://my-relay.example/ws");

    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        $"vesta-relays-test-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath))
        {
            File.Delete(_tempPath);
        }
    }

    // --- RelayResolver precedence ---

    [Fact]
    public void Resolve_NoOverrideNoManifest_ReturnsDefaultsInOrder()
    {
        IReadOnlyList<Uri> result = RelayResolver.Resolve([Default1, Default2]);

        Assert.Equal([Default1, Default2], result);
    }

    [Fact]
    public void Resolve_UserOverride_TakesPrecedenceOverEverything()
    {
        IReadOnlyList<Uri> result = RelayResolver.Resolve(
            [Default1, Default2],
            userOverride: Override,
            manifestRelays: [Manifest1, Manifest2]);

        Assert.Equal(Override, result[0]);
    }

    [Fact]
    public void Resolve_ManifestRelays_RankAboveDefaults()
    {
        IReadOnlyList<Uri> result = RelayResolver.Resolve(
            [Default1, Default2],
            userOverride: null,
            manifestRelays: [Manifest1, Manifest2]);

        Assert.Equal([Manifest1, Manifest2, Default1, Default2], result);
    }

    [Fact]
    public void Resolve_FullPrecedence_OverrideThenManifestThenDefaults()
    {
        IReadOnlyList<Uri> result = RelayResolver.Resolve(
            [Default1, Default2],
            userOverride: Override,
            manifestRelays: [Manifest1, Manifest2]);

        Assert.Equal([Override, Manifest1, Manifest2, Default1, Default2], result);
    }

    [Fact]
    public void Resolve_DeduplicatesKeepingHighestPriority()
    {
        // Override duplicates a manifest relay, and a default duplicates a manifest relay.
        IReadOnlyList<Uri> result = RelayResolver.Resolve(
            [Default1, Manifest1],
            userOverride: Manifest2,
            manifestRelays: [Manifest1, Manifest2]);

        Assert.Equal([Manifest2, Manifest1, Default1], result);
    }

    [Fact]
    public void Resolve_EmptyDefaultsWithNoOtherSources_Throws()
    {
        Assert.Throws<ArgumentException>(() => RelayResolver.Resolve([]));
    }

    // --- FileRelayOverrideStore ---

    [Fact]
    public void Override_GetBeforeSet_ReturnsNull()
    {
        FileRelayOverrideStore store = new(_tempPath);

        Assert.Null(store.GetOverride());
    }

    [Fact]
    public void Override_SetThenGet_RoundTrips()
    {
        FileRelayOverrideStore store = new(_tempPath);

        store.SetOverride(Override);

        Assert.Equal(Override, store.GetOverride());
    }

    [Fact]
    public void Override_SetThenClear_ReturnsNull()
    {
        FileRelayOverrideStore store = new(_tempPath);
        store.SetOverride(Override);

        store.ClearOverride();

        Assert.Null(store.GetOverride());
        Assert.False(File.Exists(_tempPath));
    }

    [Fact]
    public void Override_PersistsAcrossStoreInstances()
    {
        new FileRelayOverrideStore(_tempPath).SetOverride(Override);

        FileRelayOverrideStore reopened = new(_tempPath);

        Assert.Equal(Override, reopened.GetOverride());
    }

    [Fact]
    public void Override_CorruptFile_TreatedAsNoOverride()
    {
        File.WriteAllText(_tempPath, "{ this is not valid json");

        FileRelayOverrideStore store = new(_tempPath);

        Assert.Null(store.GetOverride());
    }

    [Fact]
    public void Override_FeedsResolverAsHighestPriority()
    {
        FileRelayOverrideStore store = new(_tempPath);
        store.SetOverride(Override);

        IReadOnlyList<Uri> result = RelayResolver.Resolve(
            [Default1, Default2],
            userOverride: store.GetOverride(),
            manifestRelays: [Manifest1]);

        Assert.Equal([Override, Manifest1, Default1, Default2], result);
    }
}
