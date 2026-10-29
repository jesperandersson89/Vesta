using System.Security.Cryptography;
using System.Text.Json;

namespace Vesta;

/// <summary>Options for <see cref="VestaConnection.OpenEncryptedChannelAsync"/> / <see cref="VestaConnection.CreateEncryptedChannelAsync"/>.</summary>
public sealed class EncryptedChannelOptions
{
    /// <summary>Where keys (and, for admins, member keys) live. Defaults to an <see cref="InMemoryChannelKeyStore"/>; use <see cref="SqliteChannelKeyStore"/> for anything real.</summary>
    public IChannelKeyStore? KeyStore { get; init; }

    /// <summary>
    /// Ed25519 public keys of the admins whose <c>vesta.channel-key</c> events this client trusts (the
    /// trusted-writer pattern: compiled into the app or carried by an invite). An admin's own key is added
    /// automatically when <see cref="IsAdmin"/> is set.
    /// </summary>
    public IReadOnlyList<byte[]> AdminPublicKeys { get; init; } = [];

    /// <summary>This identity is a channel admin: it answers key requests and can rotate the key.</summary>
    public bool IsAdmin { get; init; }

    /// <summary>
    /// Admin-only hook to approve a key request. The relay already restricts publishing on a private channel
    /// to members, so the default accepts every request; return <c>false</c> to hold a request back.
    /// </summary>
    public Func<ChannelKeyRequest, bool>? ApproveKeyRequest { get; init; }

    /// <summary>Catch-up position for the subscription. Defaults to 0 so historical key grants are replayed.</summary>
    public long FromSequence { get; init; }

    /// <summary>Always publish a key request on open, even when the store already holds keys (e.g. after an admin lost its member list).</summary>
    public bool AlwaysRequestKeys { get; init; }

    /// <summary>How long <see cref="VestaConnection.CreateEncryptedChannelAsync"/> waits for the relay's control ACK.</summary>
    public TimeSpan CreateTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// An end-to-end encrypted channel on a <see cref="VestaConnection"/> (PLANNING #33 phase C). Encrypts on
/// <see cref="PublishAsync"/>, decrypts incoming events into <see cref="OnEvent"/> (payload replaced by the
/// plaintext; the original ciphertext event is in <see cref="DecryptedEvent.Sealed"/>), requests keys from an
/// admin when it has none, buffers events whose epoch key has not arrived, and — when opened as admin —
/// answers key requests and rotates keys. The relay never sees a key.
/// </summary>
public sealed class EncryptedChannel : IAsyncDisposable
{
    private readonly VestaConnection _connection;
    private readonly VestaIdentity _identity;
    private readonly ChannelKeyPair _keyPair;
    private readonly IChannelKeyStore _store;
    private readonly EncryptedChannelOptions _options;
    private readonly List<byte[]> _adminKeys;
    private readonly SemaphoreSlim _adminLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<int, ChannelKey> _keys = [];
    private readonly Dictionary<int, SortedDictionary<long, SequencedEvent>> _pending = [];
    private readonly Dictionary<string, int> _grantedEpochs = [];
    private TaskCompletionSource<bool> _keyArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    /// <summary>A decrypted event: <see cref="Event"/> carries the plaintext payload, <see cref="Sealed"/> the original.</summary>
    public sealed record DecryptedEvent(SequencedEvent Event, SequencedEvent Sealed);

    public string ChannelId { get; }

    /// <summary>Whether this client acts as a channel admin.</summary>
    public bool IsAdmin => _options.IsAdmin;

    /// <summary>Fired for every app event on the channel once decrypted, in sequence order per epoch.</summary>
    public event Action<DecryptedEvent>? OnEvent;

    /// <summary>Fired when an event could not be decrypted with the key it names (tampering or a stale key).</summary>
    public event Action<SequencedEvent, Exception>? OnDecryptFailed;

    /// <summary>Fired when a new epoch key becomes available (received from an admin, or created locally).</summary>
    public event Action<int>? OnKeyAvailable;

    /// <summary>Admin only: fired after a key request was answered.</summary>
    public event Action<ChannelKeyRequest>? OnKeyGranted;

    internal EncryptedChannel(VestaConnection connection, VestaIdentity identity, string channelId, EncryptedChannelOptions options)
    {
        _connection = connection;
        _identity = identity;
        ChannelId = channelId;
        _options = options;
        _store = options.KeyStore ?? new InMemoryChannelKeyStore();
        _keyPair = ChannelKeyPair.FromIdentity(identity);
        _adminKeys = [.. options.AdminPublicKeys];
        if (options.IsAdmin && !_adminKeys.Any(k => k.AsSpan().SequenceEqual(identity.PublicKey)))
            _adminKeys.Add(identity.PublicKey);
        if (!options.IsAdmin && _adminKeys.Count == 0)
            throw new ArgumentException("A member must be given at least one admin public key to trust.", nameof(options));

        connection.OnEvent += HandleEvent;
        connection.OnEventsBatch += HandleBatch;
    }

    /// <summary>The newest epoch this client holds a key for, or <c>null</c> before the first key arrives.</summary>
    public int? CurrentEpoch
    {
        get { lock (_stateLock) return _keys.Count == 0 ? null : _keys.Keys.Max(); }
    }

    /// <summary>True once at least one epoch key is held.</summary>
    public bool HasKeys => CurrentEpoch is not null;

    /// <summary>Wait until a key is available (immediately satisfied when one already is).</summary>
    public async Task WaitForKeyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task waiter;
        lock (_stateLock)
        {
            if (_keys.Count > 0) return;
            waiter = _keyArrived.Task;
        }
        await waiter.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (ChannelKey key in await _store.LoadKeysAsync(ChannelId, cancellationToken).ConfigureAwait(false))
            AddKey(key, announce: false);

        await _connection.SubscribeAsync(ChannelId, _options.FromSequence, cancellationToken).ConfigureAwait(false);

        if (!IsAdmin && (_options.AlwaysRequestKeys || !HasKeys))
            await _connection.PublishAsync(ChannelKeyEvents.BuildKeyRequest(_identity, ChannelId, _keyPair), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Admin only: create and store the first epoch key.</summary>
    internal async Task CreateInitialKeyAsync(CancellationToken cancellationToken)
    {
        ChannelKey key = ChannelKey.Generate(1);
        await _store.SaveKeyAsync(ChannelId, key, cancellationToken).ConfigureAwait(false);
        AddKey(key, announce: true);
    }

    /// <summary>Encrypt <paramref name="payload"/> under the newest key and publish it.</summary>
    public async Task PublishAsync(string eventType, JsonElement payload, bool replace = false, JsonElement? metadata = null, Guid? parentId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (eventType.StartsWith("vesta.", StringComparison.Ordinal))
            throw new ArgumentException("Application events on an encrypted channel must not use the reserved 'vesta.' prefix.", nameof(eventType));

        ChannelKey key;
        lock (_stateLock)
        {
            if (_keys.Count == 0)
                throw new InvalidOperationException("No channel key yet; await WaitForKeyAsync before publishing.");
            key = _keys[_keys.Keys.Max()];
        }

        Guid eventId = Guid.NewGuid();
        JsonElement envelope = ChannelCrypto.EncryptPayload(key, ChannelId, eventId, eventType, payload);
        VestaEvent evt = new(
            Id: eventId,
            ChannelId: ChannelId,
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: _identity.ClientId,
            EventType: eventType,
            Payload: envelope,
            ParentId: parentId,
            Replace: replace,
            Metadata: metadata);
        await _connection.PublishAsync(evt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Admin only: start a new epoch and seal it to every member in the store. Call after
    /// <see cref="RemoveMemberAsync"/> (which does it for you) or whenever a key may have leaked.
    /// Events already published stay readable to whoever held the old key.
    /// </summary>
    public async Task RotateKeyAsync(CancellationToken cancellationToken = default)
    {
        RequireAdmin();
        await _adminLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int nextEpoch = (CurrentEpoch ?? 0) + 1;
            ChannelKey key = ChannelKey.Generate(nextEpoch);
            await _store.SaveKeyAsync(ChannelId, key, cancellationToken).ConfigureAwait(false);
            AddKey(key, announce: true);

            IReadOnlyDictionary<string, byte[]> members = await _store.LoadMembersAsync(ChannelId, cancellationToken).ConfigureAwait(false);
            foreach (KeyValuePair<string, byte[]> member in members)
                await PublishGrantAsync(member.Key, member.Value, [key], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _adminLock.Release();
        }
    }

    /// <summary>Admin only: revoke the member on the relay, forget its key, and rotate so it cannot read new events.</summary>
    public async Task RemoveMemberAsync(string clientId, CancellationToken cancellationToken = default)
    {
        RequireAdmin();
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        await _connection.RevokeAccessAsync(ChannelId, clientId, cancellationToken).ConfigureAwait(false);
        await _store.RemoveMemberAsync(ChannelId, clientId, cancellationToken).ConfigureAwait(false);
        lock (_stateLock) _grantedEpochs.Remove(clientId);
        await RotateKeyAsync(cancellationToken).ConfigureAwait(false);
    }

    private void HandleEvent(EventMessage message)
    {
        if (message.ChannelId != ChannelId) return;
        SequencedEvent sequenced = new(message.Event, message.Sequence, message.ReceivedAt);
        _ = ProcessAsync([sequenced], live: true);
    }

    private void HandleBatch(EventsBatchMessage batch)
    {
        if (batch.ChannelId != ChannelId || batch.Events.Count == 0) return;
        _ = ProcessAsync(batch.Events, live: false);
    }

    private async Task ProcessAsync(IReadOnlyList<SequencedEvent> events, bool live)
    {
        try
        {
            // Pass 1: grants first, so a historical request that was already answered is not answered again.
            foreach (SequencedEvent sequenced in events)
            {
                if (sequenced.Event.EventType != ChannelKeyEvents.KeyGrantEventType) continue;
                ChannelKeyGrant? grant = ChannelKeyEvents.TryParseKeyGrant(sequenced.Event, _adminKeys);
                if (grant is null) continue;
                lock (_stateLock)
                {
                    int max = grant.Keys.Max(k => k.Epoch);
                    if (!_grantedEpochs.TryGetValue(grant.RecipientClientId, out int known) || known < max)
                        _grantedEpochs[grant.RecipientClientId] = max;
                }
                if (grant.RecipientClientId == _identity.ClientId)
                    await OpenGrantAsync(grant).ConfigureAwait(false);
            }

            // Pass 2: requests (admin only).
            if (IsAdmin)
            {
                foreach (SequencedEvent sequenced in events)
                {
                    if (sequenced.Event.EventType != ChannelKeyEvents.KeyRequestEventType) continue;
                    ChannelKeyRequest? request = ChannelKeyEvents.TryParseKeyRequest(sequenced.Event);
                    if (request is null || request.ClientId == _identity.ClientId) continue;
                    await AnswerRequestAsync(request).ConfigureAwait(false);
                }
            }

            // Pass 3: app events, in order.
            foreach (SequencedEvent sequenced in events)
            {
                if (sequenced.Event.EventType.StartsWith("vesta.", StringComparison.Ordinal)) continue;
                Deliver(sequenced);
            }
        }
        catch (Exception ex) when (!live)
        {
            OnDecryptFailed?.Invoke(events[0], ex);
        }
    }

    private async Task OpenGrantAsync(ChannelKeyGrant grant)
    {
        foreach (SealedChannelKey sealedKey in grant.Keys)
        {
            lock (_stateLock)
            {
                if (_keys.ContainsKey(sealedKey.Epoch)) continue;
            }
            ChannelKey key;
            try
            {
                key = ChannelCrypto.OpenKey(sealedKey, ChannelId, _identity.ClientId, _keyPair);
            }
            catch (CryptographicException)
            {
                continue; // sealed to a different key of ours (e.g. before a re-pair); ignore
            }
            await _store.SaveKeyAsync(ChannelId, key).ConfigureAwait(false);
            AddKey(key, announce: true);
        }
    }

    private async Task AnswerRequestAsync(ChannelKeyRequest request)
    {
        if (_options.ApproveKeyRequest is { } approve && !approve(request))
            return;

        await _adminLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _store.SaveMemberAsync(ChannelId, request.ClientId, request.X25519PublicKey).ConfigureAwait(false);

            List<ChannelKey> keys;
            lock (_stateLock)
            {
                if (_keys.Count == 0) return;
                int current = _keys.Keys.Max();
                if (_grantedEpochs.TryGetValue(request.ClientId, out int granted) && granted >= current)
                    return;
                keys = _keys.Values.OrderBy(k => k.Epoch).ToList();
            }
            await PublishGrantAsync(request.ClientId, request.X25519PublicKey, keys).ConfigureAwait(false);
            OnKeyGranted?.Invoke(request);
        }
        finally
        {
            _adminLock.Release();
        }
    }

    private async Task PublishGrantAsync(string recipientClientId, byte[] recipientX25519, IReadOnlyList<ChannelKey> keys, CancellationToken cancellationToken = default)
    {
        List<SealedChannelKey> sealedKeys = keys.Select(k => ChannelCrypto.SealKey(k, ChannelId, recipientClientId, recipientX25519)).ToList();
        VestaEvent grant = ChannelKeyEvents.BuildKeyGrant(_identity, ChannelId, recipientClientId, sealedKeys);
        lock (_stateLock)
        {
            int max = keys.Max(k => k.Epoch);
            if (!_grantedEpochs.TryGetValue(recipientClientId, out int known) || known < max)
                _grantedEpochs[recipientClientId] = max;
        }
        await _connection.PublishAsync(grant, cancellationToken).ConfigureAwait(false);
    }

    private void Deliver(SequencedEvent sequenced)
    {
        int epoch;
        try
        {
            epoch = ChannelCrypto.ReadEpoch(sequenced.Event.Payload);
        }
        catch (FormatException ex)
        {
            OnDecryptFailed?.Invoke(sequenced, ex);
            return;
        }

        ChannelKey? key;
        lock (_stateLock)
        {
            if (!_keys.TryGetValue(epoch, out key))
            {
                if (!_pending.TryGetValue(epoch, out SortedDictionary<long, SequencedEvent>? queue))
                    _pending[epoch] = queue = [];
                queue[sequenced.Sequence] = sequenced;
                return;
            }
        }
        Decrypt(sequenced, key);
    }

    private void Decrypt(SequencedEvent sequenced, ChannelKey key)
    {
        try
        {
            JsonElement plaintext = ChannelCrypto.DecryptPayload(key, ChannelId, sequenced.Event.Id, sequenced.Event.EventType, sequenced.Event.Payload);
            SequencedEvent decrypted = sequenced with { Event = sequenced.Event with { Payload = plaintext } };
            OnEvent?.Invoke(new DecryptedEvent(decrypted, sequenced));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            OnDecryptFailed?.Invoke(sequenced, ex);
        }
    }

    private void AddKey(ChannelKey key, bool announce)
    {
        List<SequencedEvent>? flush = null;
        TaskCompletionSource<bool> arrived;
        lock (_stateLock)
        {
            _keys[key.Epoch] = key;
            if (_pending.Remove(key.Epoch, out SortedDictionary<long, SequencedEvent>? queue))
                flush = queue.Values.ToList();
            arrived = _keyArrived;
        }
        arrived.TrySetResult(true);
        if (announce) OnKeyAvailable?.Invoke(key.Epoch);
        if (flush is not null)
        {
            foreach (SequencedEvent sequenced in flush)
                Decrypt(sequenced, key);
        }
    }

    private void RequireAdmin()
    {
        if (!IsAdmin)
            throw new InvalidOperationException("Only a channel admin (EncryptedChannelOptions.IsAdmin) can do this.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.OnEvent -= HandleEvent;
        _connection.OnEventsBatch -= HandleBatch;
        _keyPair.Dispose();
        _adminLock.Dispose();
        await Task.CompletedTask;
    }
}
