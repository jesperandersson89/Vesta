using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using VestaCore.Channels;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;
using VestaCore.Storage;

namespace VestaServer.Connections;

/// <summary>
/// Handles the protocol message loop for a single connected client.
/// Processes incoming messages, interacts with the event store,
/// and coordinates broadcasts through the connection manager.
/// </summary>
public sealed class ProtocolHandler(
    IEventStore eventStore,
    ConnectionManager connectionManager,
    ILogger<ProtocolHandler> logger)
{
    private static readonly string ServerId = Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Runs the full message loop for a client connection until disconnect.
    /// </summary>
    public async Task HandleConnectionAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        connectionManager.Add(connection);
        logger.LogInformation("Client connected: {ConnectionId}", connection.ConnectionId);

        try
        {
            while (connection.IsOpen && !cancellationToken.IsCancellationRequested)
            {
                ProtocolMessage? message = await connection.ReceiveAsync(cancellationToken);

                if (message is null)
                    break;

                await HandleMessageAsync(connection, message, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (WebSocketException)
        {
            // Client disconnected without completing close handshake — not an error
            logger.LogDebug("Client {ConnectionId} disconnected abruptly", connection.ConnectionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling client {ConnectionId}", connection.ConnectionId);
        }
        finally
        {
            connectionManager.Remove(connection);
            logger.LogInformation("Client disconnected: {ConnectionId} (ClientId: {ClientId})",
                connection.ConnectionId, connection.ClientId ?? "unknown");
        }
    }

    private async Task HandleMessageAsync(
        ClientConnection connection,
        ProtocolMessage message,
        CancellationToken cancellationToken)
    {
        switch (message)
        {
            case HelloMessage hello:
                await HandleHelloAsync(connection, hello, cancellationToken);
                break;

            case PublishMessage publish:
                await HandlePublishAsync(connection, publish, cancellationToken);
                break;

            case SubscribeMessage subscribe:
                await HandleSubscribeAsync(connection, subscribe, cancellationToken);
                break;

            case UnsubscribeMessage unsubscribe:
                HandleUnsubscribe(connection, unsubscribe);
                break;

            case FetchMessage fetch:
                await HandleFetchAsync(connection, fetch, cancellationToken);
                break;

            default:
                await connection.SendAsync(
                    new ErrorMessage("UNKNOWN_MESSAGE", "Unrecognized message type"),
                    cancellationToken);
                break;
        }
    }

    private async Task HandleHelloAsync(
        ClientConnection connection,
        HelloMessage hello,
        CancellationToken cancellationToken)
    {
        connection.ClientId = hello.ClientId;

        // If a public key is provided, validate it matches the clientId and store it
        if (!string.IsNullOrEmpty(hello.PublicKey))
        {
            byte[] publicKeyBytes = Base64UrlDecode(hello.PublicKey);
            string derivedClientId = VestaIdentity.DeriveClientId(publicKeyBytes);

            if (derivedClientId != hello.ClientId)
            {
                await connection.SendAsync(
                    new ErrorMessage("IDENTITY_MISMATCH", "PublicKey does not match clientId"),
                    cancellationToken);
                return;
            }

            connection.PublicKey = publicKeyBytes;
        }

        // Subscribe to requested channels
        foreach (string channelId in hello.Channels)
        {
            if (!ChannelId.IsValid(channelId))
            {
                await connection.SendAsync(
                    new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{channelId}'"),
                    cancellationToken);
                continue;
            }

            connection.Subscriptions.Add(channelId);
        }

        // Send WELCOME
        await connection.SendAsync(
            new WelcomeMessage(ServerId, connection.Subscriptions.ToList()),
            cancellationToken);

        // Catch-up: send missed events for channels with lastSequences
        foreach (KeyValuePair<string, long> entry in hello.LastSequences)
        {
            if (!connection.Subscriptions.Contains(entry.Key))
                continue;

            long fromSequence = entry.Value + 1; // Client has seen up to entry.Value
            IReadOnlyList<SequencedEvent> missedEvents = await eventStore.GetEventsAsync(
                entry.Key, fromSequence, cancellationToken: cancellationToken);

            if (missedEvents.Count > 0)
            {
                await connection.SendAsync(
                    new EventsBatchMessage(entry.Key, missedEvents),
                    cancellationToken);
            }
        }

        logger.LogInformation("Client {ClientId} said HELLO, subscribed to {ChannelCount} channels",
            hello.ClientId, connection.Subscriptions.Count);
    }

    private async Task HandlePublishAsync(
        ClientConnection connection,
        PublishMessage publish,
        CancellationToken cancellationToken)
    {
        if (!ChannelId.IsValid(publish.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{publish.ChannelId}'"),
                cancellationToken);
            return;
        }

        // Verify signature if public key is known
        if (connection.PublicKey is not null)
        {
            if (string.IsNullOrEmpty(publish.Event.Signature))
            {
                await connection.SendAsync(
                    new ErrorMessage("SIGNATURE_REQUIRED", "Events must be signed when public key is registered"),
                    cancellationToken);
                return;
            }

            if (!EventSigner.VerifyEvent(publish.Event, connection.PublicKey))
            {
                await connection.SendAsync(
                    new ErrorMessage("INVALID_SIGNATURE", "Event signature verification failed"),
                    cancellationToken);
                return;
            }
        }

        // Store the event
        SequencedEvent sequenced = await eventStore.AppendAsync(publish.Event, cancellationToken);

        // ACK back to the publisher
        await connection.SendAsync(
            new AckMessage(publish.ChannelId, sequenced.Event.Id, sequenced.Sequence),
            cancellationToken);

        // Broadcast to other subscribers
        EventMessage eventMessage = new(
            publish.ChannelId,
            sequenced.Event,
            sequenced.Sequence,
            sequenced.ReceivedAt);

        await connectionManager.BroadcastToChannelAsync(
            publish.ChannelId,
            eventMessage,
            excludeConnectionId: connection.ConnectionId,
            cancellationToken: cancellationToken);
    }

    private async Task HandleSubscribeAsync(
        ClientConnection connection,
        SubscribeMessage subscribe,
        CancellationToken cancellationToken)
    {
        if (!ChannelId.IsValid(subscribe.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{subscribe.ChannelId}'"),
                cancellationToken);
            return;
        }

        connection.Subscriptions.Add(subscribe.ChannelId);

        // If fromSequence is specified, send catch-up events
        if (subscribe.FromSequence.HasValue)
        {
            IReadOnlyList<SequencedEvent> events = await eventStore.GetEventsAsync(
                subscribe.ChannelId,
                subscribe.FromSequence.Value,
                cancellationToken: cancellationToken);

            if (events.Count > 0)
            {
                await connection.SendAsync(
                    new EventsBatchMessage(subscribe.ChannelId, events),
                    cancellationToken);
            }
        }
    }

    private void HandleUnsubscribe(ClientConnection connection, UnsubscribeMessage unsubscribe)
    {
        connection.Subscriptions.Remove(unsubscribe.ChannelId);
    }

    private async Task HandleFetchAsync(
        ClientConnection connection,
        FetchMessage fetch,
        CancellationToken cancellationToken)
    {
        if (!ChannelId.IsValid(fetch.ChannelId))
        {
            await connection.SendAsync(
                new ErrorMessage("INVALID_CHANNEL", $"Invalid channel ID: '{fetch.ChannelId}'"),
                cancellationToken);
            return;
        }

        int limit = fetch.Limit ?? 100;
        IReadOnlyList<SequencedEvent> events = await eventStore.GetEventsAsync(
            fetch.ChannelId,
            fetch.FromSequence,
            limit,
            cancellationToken);

        // If toSequence is specified, filter further
        if (fetch.ToSequence.HasValue)
        {
            events = events.Where(e => e.Sequence <= fetch.ToSequence.Value).ToList();
        }

        await connection.SendAsync(
            new EventsBatchMessage(fetch.ChannelId, events),
            cancellationToken);
    }

    private static byte[] Base64UrlDecode(string base64Url)
    {
        string base64 = base64Url
            .Replace('-', '+')
            .Replace('_', '/');

        int padding = (4 - base64.Length % 4) % 4;
        base64 += new string('=', padding);

        return Convert.FromBase64String(base64);
    }
}
