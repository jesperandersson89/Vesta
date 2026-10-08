using VestaServer.Storage;

namespace VestaServer.Tests;

public class AppAlertEvaluatorTests
{
  private static readonly DateOnly Period = new(2026, 10, 1);
  private static readonly int[] Thresholds = [80, 100];

  [Fact]
  public void Evaluate_BelowFirstThreshold_ReturnsNoCrossings()
  {
    IReadOnlyList<AppAlertCrossing> result = AppAlertEvaluator.Evaluate(
        new AppQuotas(MaxMessagesPerMonth: 100), messages: 79, Period, storageBytes: null, channelCount: null, Thresholds);

    Assert.Empty(result);
  }

  [Fact]
  public void Evaluate_ExactlyAtThreshold_Crosses()
  {
    IReadOnlyList<AppAlertCrossing> result = AppAlertEvaluator.Evaluate(
        new AppQuotas(MaxMessagesPerMonth: 100), messages: 80, Period, null, null, Thresholds);

    AppAlertCrossing crossing = Assert.Single(result);
    Assert.Equal(AppAlertMetrics.Messages, crossing.Metric);
    Assert.Equal(80, crossing.ThresholdPercent);
    Assert.Equal(Period, crossing.PeriodStart);
  }

  [Fact]
  public void Evaluate_AtOrOverLimit_ReturnsEveryCrossedThreshold()
  {
    IReadOnlyList<AppAlertCrossing> result = AppAlertEvaluator.Evaluate(
        new AppQuotas(TotalStorageBytes: 1_000), messages: null, Period, storageBytes: 1_500, channelCount: null, Thresholds);

    Assert.Equal([80, 100], result.Select(c => c.ThresholdPercent));
    Assert.All(result, c => Assert.Equal(AppAlertMetrics.Storage, c.Metric));
    Assert.All(result, c => Assert.Null(c.PeriodStart));
  }

  [Fact]
  public void Evaluate_NoQuotaOrUnmeasured_Skips()
  {
    IReadOnlyList<AppAlertCrossing> result = AppAlertEvaluator.Evaluate(
        new AppQuotas(MaxChannels: 10, TotalStorageBytes: 0), messages: 1_000_000, Period, storageBytes: 500, channelCount: null, Thresholds);

    Assert.Empty(result);
  }

  [Fact]
  public void Evaluate_MultipleMetrics_ReturnsEachIndependently()
  {
    IReadOnlyList<AppAlertCrossing> result = AppAlertEvaluator.Evaluate(
        new AppQuotas(MaxChannels: 10, MaxMessagesPerMonth: 100), messages: 100, Period, null, channelCount: 8, Thresholds);

    Assert.Equal(3, result.Count);
    Assert.Single(result, c => c.Metric == AppAlertMetrics.Channels);
    Assert.Equal(2, result.Count(c => c.Metric == AppAlertMetrics.Messages));
  }
}

public class InMemoryAppAlertStoreTests
{
  private static readonly DateOnly October = new(2026, 10, 1);
  private static readonly DateOnly November = new(2026, 11, 1);

  private static InMemoryAppAlertStore CreateStore() => new(TimeProvider.System);

  private static AppAlertCrossing Messages(int threshold, long observed, DateOnly period)
      => new(AppAlertMetrics.Messages, threshold, observed, 100, period);

  [Fact]
  public async Task Sync_NewCrossing_RaisesOpenAlert()
  {
    InMemoryAppAlertStore store = CreateStore();

    await store.SyncAsync("app", [Messages(80, 85, October)]);

    AppAlert alert = Assert.Single(await store.ListAsync("app", activeOnly: true));
    Assert.Equal(85, alert.Observed);
    Assert.Null(alert.ResolvedAt);
  }

  [Fact]
  public async Task Sync_SameCrossingTwice_RefreshesObservedWithoutDuplicating()
  {
    InMemoryAppAlertStore store = CreateStore();
    await store.SyncAsync("app", [Messages(80, 85, October)]);

    await store.SyncAsync("app", [Messages(80, 92, October)]);

    AppAlert alert = Assert.Single(await store.ListAsync("app", activeOnly: false));
    Assert.Equal(92, alert.Observed);
  }

  [Fact]
  public async Task Sync_NoLongerCrossing_ResolvesAlert()
  {
    InMemoryAppAlertStore store = CreateStore();
    await store.SyncAsync("app", [Messages(80, 85, October)]);

    await store.SyncAsync("app", []);

    Assert.Empty(await store.ListAsync("app", activeOnly: true));
    Assert.NotNull(Assert.Single(await store.ListAsync("app", activeOnly: false)).ResolvedAt);
  }

  [Fact]
  public async Task Sync_PeriodRollover_ResolvesOldAlertAndRaisesNewOne()
  {
    InMemoryAppAlertStore store = CreateStore();
    await store.SyncAsync("app", [Messages(80, 85, October)]);

    await store.SyncAsync("app", [Messages(80, 81, November)]);

    IReadOnlyList<AppAlert> all = await store.ListAsync("app", activeOnly: false);
    Assert.Equal(2, all.Count);
    AppAlert open = Assert.Single(all, a => a.ResolvedAt is null);
    Assert.Equal(November, open.PeriodStart);
  }

  [Fact]
  public async Task Sync_OnlyAffectsTheGivenApp()
  {
    InMemoryAppAlertStore store = CreateStore();
    await store.SyncAsync("a", [Messages(80, 85, October)]);
    await store.SyncAsync("b", [Messages(80, 85, October)]);

    await store.SyncAsync("a", []);

    Assert.Empty(await store.ListAsync("a", activeOnly: true));
    Assert.Single(await store.ListAsync("b", activeOnly: true));
  }

  [Fact]
  public async Task CountActiveByApp_CountsOnlyOpenAlerts()
  {
    InMemoryAppAlertStore store = CreateStore();
    await store.SyncAsync("a", [Messages(80, 85, October), Messages(100, 100, October)]);
    await store.SyncAsync("b", [Messages(80, 85, October)]);
    await store.SyncAsync("b", []);

    IReadOnlyDictionary<string, int> counts = await store.CountActiveByAppAsync();

    Assert.Equal(2, counts["a"]);
    Assert.False(counts.ContainsKey("b"));
  }

  [Fact]
  public async Task Acknowledge_ExistingAlert_StampsAndKeepsOpen()
  {
    InMemoryAppAlertStore store = CreateStore();
    await store.SyncAsync("app", [Messages(80, 85, October)]);
    long id = (await store.ListAsync("app", true))[0].Id;

    Assert.True(await store.AcknowledgeAsync(id));
    Assert.False(await store.AcknowledgeAsync(id + 100));

    AppAlert alert = Assert.Single(await store.ListAsync("app", activeOnly: true));
    Assert.NotNull(alert.AcknowledgedAt);
  }
}
