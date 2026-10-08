using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VestaCore.Events;
using VestaCore.Serialization;
using VestaCore.Storage;
using VestaServer.Storage;

namespace VestaServer.Admin;

/// <summary>
/// Writes an app's data as JSON Lines: one <c>manifest</c> line (app row, channels, members)
/// followed by one <c>event</c> line per stored event, channel by channel in sequence order.
/// Events past their TTL are excluded, matching what a client catch-up would see.
/// </summary>
public static class AppExportWriter
{
  public const int FormatVersion = 1;
  private const int PageSize = 1000;
  private static readonly byte[] NewLine = "\n"u8.ToArray();

  // The shared Vesta options only know the source-generated protocol types; the export envelope needs reflection too.
  private static readonly JsonSerializerOptions Json = new(VestaJsonOptions.Default)
  {
    TypeInfoResolver = JsonTypeInfoResolver.Combine(VestaJsonContext.Default, new DefaultJsonTypeInfoResolver()),
  };

  public static async Task WriteAsync(
      Stream output,
      AppInfo app,
      IChannelAccessStore channels,
      IEventStore events,
      DateTimeOffset exportedAt,
      CancellationToken cancellationToken)
  {
    IReadOnlyList<ChannelSummary> summaries = await channels.ListChannelsAsync(app.Id, includeDeleted: true, cancellationToken);
    List<ChannelSummary> ordered = [.. summaries.OrderBy(c => c.Id, StringComparer.Ordinal)];

    List<ExportChannel> manifestChannels = [];
    foreach (ChannelSummary channel in ordered)
    {
      IReadOnlyList<ChannelMember> members = await channels.ListMembersAsync(channel.Id, cancellationToken);
      manifestChannels.Add(new ExportChannel(
          channel.Id,
          channel.Visibility.ToString().ToLowerInvariant(),
          channel.CreatedAt,
          channel.DeletedAt,
          [.. members.Select(m => new ExportMember(m.ClientId, m.Role))]));
    }

    ExportApp exportApp = new(app.Id, app.OwnerClientId, app.CreatedAt, app.Quotas, app.Discoverable, app.DeletedAt);
    await WriteLineAsync(output, new ExportManifestLine("manifest", FormatVersion, exportedAt, exportApp, manifestChannels), cancellationToken);

    foreach (ChannelSummary channel in ordered)
    {
      long next = 1;
      while (true)
      {
        IReadOnlyList<SequencedEvent> page = await events.GetEventsAsync(channel.Id, next, PageSize, cancellationToken);
        foreach (SequencedEvent sequenced in page)
          await WriteLineAsync(output, new ExportEventLine("event", sequenced.Sequence, sequenced.ReceivedAt, sequenced.Event), cancellationToken);

        if (page.Count < PageSize)
          break;
        next = page[^1].Sequence + 1;
      }
      await output.FlushAsync(cancellationToken);
    }
  }

  private static async Task WriteLineAsync<T>(Stream output, T line, CancellationToken cancellationToken)
  {
    await JsonSerializer.SerializeAsync(output, line, Json, cancellationToken);
    await output.WriteAsync(NewLine, cancellationToken);
  }

  public sealed record ExportManifestLine(string Type, int Version, DateTimeOffset ExportedAt, ExportApp App, IReadOnlyList<ExportChannel> Channels);

  public sealed record ExportApp(string Id, string OwnerClientId, DateTimeOffset CreatedAt, AppQuotas Quotas, bool Discoverable, DateTimeOffset? DeletedAt);

  public sealed record ExportChannel(string Id, string Visibility, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt, IReadOnlyList<ExportMember> Members);

  public sealed record ExportMember(string ClientId, string Role);

  public sealed record ExportEventLine(string Type, long Sequence, DateTimeOffset ReceivedAt, VestaEvent Event);
}
