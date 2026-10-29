using Vesta;

namespace VestaClient.Tests;

public class ChannelKeyStoreTests
{
    public static IEnumerable<object[]> Stores()
    {
        yield return [new InMemoryChannelKeyStore()];
        yield return [SqliteChannelKeyStore.CreateInMemory()];
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Keys_SaveLoad_OverwriteSameEpoch_AndAreScopedByChannel(IChannelKeyStore store)
    {
        ChannelKey one = ChannelKey.Generate(1);
        ChannelKey two = ChannelKey.Generate(2);
        await store.SaveKeyAsync("a/room", one);
        await store.SaveKeyAsync("a/room", two);
        await store.SaveKeyAsync("b/room", ChannelKey.Generate(1));

        IReadOnlyList<ChannelKey> keys = await store.LoadKeysAsync("a/room");
        Assert.Equal([1, 2], keys.Select(k => k.Epoch).OrderBy(e => e));
        Assert.Equal(two.Key, keys.Single(k => k.Epoch == 2).Key);

        ChannelKey replaced = ChannelKey.Generate(2);
        await store.SaveKeyAsync("a/room", replaced);
        Assert.Equal(replaced.Key, (await store.LoadKeysAsync("a/room")).Single(k => k.Epoch == 2).Key);
        Assert.Empty(await store.LoadKeysAsync("never/opened"));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Members_SaveLoadRemove(IChannelKeyStore store)
    {
        byte[] key = Enumerable.Repeat((byte)7, 32).ToArray();
        await store.SaveMemberAsync("a/room", "alice", key);
        await store.SaveMemberAsync("a/room", "bob", new byte[32]);

        IReadOnlyDictionary<string, byte[]> members = await store.LoadMembersAsync("a/room");
        Assert.Equal(2, members.Count);
        Assert.Equal(key, members["alice"]);

        await store.RemoveMemberAsync("a/room", "alice");
        Assert.Equal(["bob"], (await store.LoadMembersAsync("a/room")).Keys);
        await store.RemoveMemberAsync("a/room", "nobody");
        Assert.Empty(await store.LoadMembersAsync("other/room"));
    }
}
