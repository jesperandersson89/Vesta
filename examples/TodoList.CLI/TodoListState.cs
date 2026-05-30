using System.Text.Json;
using VestaCore.Events;

namespace TodoList.CLI;

// ─── Event Schema ────────────────────────────────────────────────────────────
//
// Channel: "todo/{list-name}"
//
// Event types:
//   app.todo.item-added     { id: string (guid), title: string, createdBy: string }
//   app.todo.item-toggled   { id: string (guid), done: bool }
//   app.todo.item-renamed   { id: string (guid), title: string }
//   app.todo.item-removed   { id: string (guid) }
//
// Conflict resolution:
//   - item-added: append-only (all adds are valid, no conflicts)
//   - item-toggled/renamed: last-writer-wins by event timestamp
//   - item-removed: tombstone — item no longer shows in list
//
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Represents a single todo item, projected from the event log.
/// </summary>
public sealed class TodoItem
{
    public required Guid Id { get; init; }
    public required string Title { get; set; }
    public bool Done { get; set; }
    public required string CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastModified { get; set; }
    public bool Removed { get; set; }
}

/// <summary>
/// Projection that reconstructs the full todo list state from the event log.
/// Uses append-only for additions and LWW (by timestamp) for mutations.
/// </summary>
public sealed class TodoListState
{
    private readonly Dictionary<Guid, TodoItem> _items = new();

    /// <summary>
    /// All active (non-removed) items, in creation order.
    /// </summary>
    public IReadOnlyList<TodoItem> Items =>
        _items.Values
            .Where(i => !i.Removed)
            .OrderBy(i => i.CreatedAt)
            .ToList();

    /// <summary>
    /// The last sequence number processed.
    /// </summary>
    public long LastSequence { get; private set; }

    /// <summary>
    /// Apply a batch of sequenced events to rebuild/update state.
    /// </summary>
    public void Apply(IEnumerable<SequencedEvent> events)
    {
        foreach (SequencedEvent sequenced in events)
        {
            Apply(sequenced);
        }
    }

    /// <summary>
    /// Apply a single sequenced event.
    /// </summary>
    public void Apply(SequencedEvent sequenced)
    {
        VestaEvent evt = sequenced.Event;
        LastSequence = sequenced.Sequence;

        switch (evt.EventType)
        {
            case "app.todo.item-added":
                ApplyItemAdded(evt);
                break;
            case "app.todo.item-toggled":
                ApplyItemToggled(evt);
                break;
            case "app.todo.item-renamed":
                ApplyItemRenamed(evt);
                break;
            case "app.todo.item-removed":
                ApplyItemRemoved(evt);
                break;
        }
    }

    /// <summary>
    /// Apply a locally-created event (not yet sequenced) for optimistic display.
    /// </summary>
    public void ApplyLocal(VestaEvent evt)
    {
        switch (evt.EventType)
        {
            case "app.todo.item-added":
                ApplyItemAdded(evt);
                break;
            case "app.todo.item-toggled":
                ApplyItemToggled(evt);
                break;
            case "app.todo.item-renamed":
                ApplyItemRenamed(evt);
                break;
            case "app.todo.item-removed":
                ApplyItemRemoved(evt);
                break;
        }
    }

    private void ApplyItemAdded(VestaEvent evt)
    {
        string idStr = evt.Payload.GetProperty("id").GetString()!;
        Guid id = Guid.Parse(idStr);

        if (_items.ContainsKey(id))
            return; // Duplicate add — idempotent

        string title = evt.Payload.GetProperty("title").GetString()!;
        string createdBy = evt.Payload.TryGetProperty("createdBy", out JsonElement cb)
            ? cb.GetString() ?? evt.ClientId[..8]
            : evt.ClientId[..8];

        _items[id] = new TodoItem
        {
            Id = id,
            Title = title,
            Done = false,
            CreatedBy = createdBy,
            CreatedAt = evt.Timestamp,
            LastModified = evt.Timestamp
        };
    }

    private void ApplyItemToggled(VestaEvent evt)
    {
        string idStr = evt.Payload.GetProperty("id").GetString()!;
        Guid id = Guid.Parse(idStr);

        if (!_items.TryGetValue(id, out TodoItem? item))
            return; // Item doesn't exist (yet) — skip

        // LWW: only apply if this event is newer
        if (evt.Timestamp <= item.LastModified)
            return;

        item.Done = evt.Payload.GetProperty("done").GetBoolean();
        item.LastModified = evt.Timestamp;
    }

    private void ApplyItemRenamed(VestaEvent evt)
    {
        string idStr = evt.Payload.GetProperty("id").GetString()!;
        Guid id = Guid.Parse(idStr);

        if (!_items.TryGetValue(id, out TodoItem? item))
            return;

        if (evt.Timestamp <= item.LastModified)
            return;

        item.Title = evt.Payload.GetProperty("title").GetString()!;
        item.LastModified = evt.Timestamp;
    }

    private void ApplyItemRemoved(VestaEvent evt)
    {
        string idStr = evt.Payload.GetProperty("id").GetString()!;
        Guid id = Guid.Parse(idStr);

        if (!_items.TryGetValue(id, out TodoItem? item))
            return;

        if (evt.Timestamp <= item.LastModified)
            return;

        item.Removed = true;
        item.LastModified = evt.Timestamp;
    }
}
