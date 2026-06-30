using VestaCore.Identity;
using VestaCore.Relay;
using VestaServer.Federation;

namespace VestaServer.Tests;

public class InMemoryServerDirectoryTests
{
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static ServerDescriptor SignedDescriptor(
        VestaIdentity relay,
        DateTimeOffset issuedAt,
        int ttlSeconds = 300,
        params string[] appIds)
    {
        ServerDescriptor descriptor = new()
        {
            RelayPublicKey = string.Empty,
            Urls = ["wss://relay.example/ws"],
            Apps = [.. appIds.Select(a => new DiscoverableApp(a, "owner-id"))],
            IssuedAt = issuedAt,
            TtlSeconds = ttlSeconds
        };
        return DescriptorSigner.Sign(descriptor, relay);
    }

    [Fact]
    public void Merge_AcceptsNewDescriptor()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryServerDirectory directory = new(time, maxPeers: 16);
        using VestaIdentity relay = VestaIdentity.Generate();

        bool accepted = directory.Merge(SignedDescriptor(relay, time.GetUtcNow(), appIds: "chess"));

        Assert.True(accepted);
        Assert.Single(directory.All());
    }

    [Fact]
    public void Merge_RejectsOlderDescriptorForSameRelay()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryServerDirectory directory = new(time, maxPeers: 16);
        using VestaIdentity relay = VestaIdentity.Generate();

        DateTimeOffset newer = time.GetUtcNow();
        DateTimeOffset older = newer.AddMinutes(-1);

        Assert.True(directory.Merge(SignedDescriptor(relay, newer, appIds: "chess")));
        Assert.False(directory.Merge(SignedDescriptor(relay, older, appIds: "chess")));
        Assert.Single(directory.All());
    }

    [Fact]
    public void Merge_NewerDescriptorSupersedes()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryServerDirectory directory = new(time, maxPeers: 16);
        using VestaIdentity relay = VestaIdentity.Generate();

        Assert.True(directory.Merge(SignedDescriptor(relay, time.GetUtcNow(), appIds: "chess")));
        Assert.True(directory.Merge(SignedDescriptor(relay, time.GetUtcNow().AddMinutes(1), 300, "chess", "chat")));

        ServerDescriptor only = Assert.Single(directory.All());
        Assert.Equal(2, only.Apps.Count);
    }

    [Fact]
    public void All_EvictsExpiredDescriptors()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryServerDirectory directory = new(time, maxPeers: 16);
        using VestaIdentity relay = VestaIdentity.Generate();

        directory.Merge(SignedDescriptor(relay, time.GetUtcNow(), ttlSeconds: 60, appIds: "chess"));
        Assert.Single(directory.All());

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Empty(directory.All());
    }

    [Fact]
    public void ForApp_ReturnsOnlyRelaysHostingApp()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryServerDirectory directory = new(time, maxPeers: 16);
        using VestaIdentity relayA = VestaIdentity.Generate();
        using VestaIdentity relayB = VestaIdentity.Generate();

        directory.Merge(SignedDescriptor(relayA, time.GetUtcNow(), appIds: "chess"));
        directory.Merge(SignedDescriptor(relayB, time.GetUtcNow(), appIds: "chat"));

        ServerDescriptor match = Assert.Single(directory.ForApp("chess"));
        Assert.Contains(match.Apps, a => a.AppId == "chess");
        Assert.Empty(directory.ForApp("nope"));
    }

    [Fact]
    public void Merge_EnforcesMaxPeersByEvictingOldest()
    {
        MutableTimeProvider time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        InMemoryServerDirectory directory = new(time, maxPeers: 2);

        using VestaIdentity relayA = VestaIdentity.Generate();
        using VestaIdentity relayB = VestaIdentity.Generate();
        using VestaIdentity relayC = VestaIdentity.Generate();

        directory.Merge(SignedDescriptor(relayA, time.GetUtcNow().AddSeconds(0), appIds: "a"));
        directory.Merge(SignedDescriptor(relayB, time.GetUtcNow().AddSeconds(1), appIds: "b"));
        directory.Merge(SignedDescriptor(relayC, time.GetUtcNow().AddSeconds(2), appIds: "c"));

        IReadOnlyList<ServerDescriptor> all = directory.All();
        Assert.Equal(2, all.Count);
        // The oldest (relay A) should have been evicted.
        Assert.DoesNotContain(all, d => d.Apps.Any(a => a.AppId == "a"));
    }
}
