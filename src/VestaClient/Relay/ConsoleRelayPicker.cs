namespace VestaClient.Relay;

/// <summary>
/// A plain-text view over <see cref="RelayRecoverySession"/> for CLI apps. Shows why relays failed,
/// lets the user retry, discover, enter a URL, or pick a discovered relay, and returns once the
/// connection is healthy again or the user dismisses the prompt.
/// </summary>
public static class ConsoleRelayPicker
{
    /// <summary>Runs the prompt loop. Returns true if the connection recovered, false if dismissed or input ended.</summary>
    public static async Task<bool> RunAsync(
        RelayRecoverySession session,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        while (!cancellationToken.IsCancellationRequested)
        {
            RelayRecoverySnapshot snapshot = session.Snapshot;
            if (snapshot.Phase == RelayRecoveryPhase.Healthy)
            {
                await output.WriteLineAsync("Connected.");
                return true;
            }

            await RenderAsync(snapshot, output);

            string? line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return false;
            }

            string command = line.Trim();
            if (command.Length == 0)
            {
                continue;
            }

            if (int.TryParse(command, out int number) && number >= 1 && number <= snapshot.Choices.Count)
            {
                RelayChoice choice = snapshot.Choices[number - 1];
                if (!choice.HostsRequestedApp && !await ConfirmAsync(input, output, choice, cancellationToken))
                {
                    continue;
                }
                await session.AdoptAsync(choice, cancellationToken);
                continue;
            }

            switch (command.ToLowerInvariant().Split(' ', 2)[0])
            {
                case "r":
                    await session.RetryAsync(cancellationToken);
                    break;
                case "d":
                    await session.DiscoverAsync(cancellationToken);
                    break;
                case "u":
                    string[] parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    await session.UseManualAsync(parts.Length > 1 ? parts[1] : string.Empty, cancellationToken);
                    break;
                case "c":
                    await session.ClearOverrideAsync(cancellationToken);
                    break;
                case "q":
                    session.Dismiss();
                    return false;
                default:
                    await output.WriteLineAsync("Unknown command.");
                    break;
            }
        }

        return false;
    }

    private static async Task<bool> ConfirmAsync(
        TextReader input,
        TextWriter output,
        RelayChoice choice,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            $"{choice.Url} is not verified to host this app. It may not have your data, and a relay you don't trust can see what you send it.");
        await output.WriteAsync("Connect anyway? [y/N] ");
        string? answer = await input.ReadLineAsync(cancellationToken);
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RenderAsync(RelayRecoverySnapshot snapshot, TextWriter output)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync(snapshot.Phase switch
        {
            RelayRecoveryPhase.Degraded => "Connection lost. Retrying...",
            RelayRecoveryPhase.Exhausted => "Can't reach any relay for this app.",
            RelayRecoveryPhase.Discovering => "Looking for other relays...",
            RelayRecoveryPhase.Choices => "Other relays found:",
            RelayRecoveryPhase.NothingFound => "No other relays found.",
            RelayRecoveryPhase.Adopting => $"Connecting to {snapshot.Adopting}...",
            RelayRecoveryPhase.Failed => snapshot.FailureReason ?? "Failed.",
            _ => string.Empty,
        });

        foreach (RelayAttempt attempt in snapshot.TriedRelays)
        {
            await output.WriteLineAsync($"  x {attempt.Relay} - {attempt.Reason}");
        }

        for (int i = 0; i < snapshot.Choices.Count; i++)
        {
            RelayChoice choice = snapshot.Choices[i];
            string tag = choice.HostsRequestedApp ? "hosts this app" : "unverified";
            string cached = choice.FromCache ? ", remembered" : string.Empty;
            await output.WriteLineAsync($"  {i + 1}. {choice.Url} ({tag}{cached})");
        }

        if (snapshot.BackgroundRetrying)
        {
            await output.WriteLineAsync("  (still retrying in the background)");
        }
        string clear = snapshot.ActiveOverride is null ? string.Empty : ", [c]lear saved relay";
        await output.WriteAsync($"[r]etry, [d]iscover, [u]se <url>{clear}, [q]uit prompt, or a number > ");
    }
}
