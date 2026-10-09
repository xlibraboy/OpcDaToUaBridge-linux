using System.Text.Json;
using Microsoft.Extensions.Options;
using OpcBridge.Client;
using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// Append-only operator notes attached to logic blocks (<c>logic-notes.json</c>). The phone
/// posts notes through <c>POST /api/logic/notes</c>; the file keeps the newest
/// <see cref="MaxNotes"/> entries so it can never grow without bound on a plant host.
/// </summary>
public sealed class LogicNoteStore
{
    public const int MaxNotes = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object sync_ = new();
    private readonly string persist_path_;
    private List<LogicNoteDto> notes_;

    public LogicNoteStore(IOptions<BridgeOptions> options)
    {
        _ = options;
        persist_path_ = DataDirectory.Combine("logic-notes.json");
        notes_ = LoadFromDisk() ?? new List<LogicNoteDto>();
    }

    /// <summary>Newest-first notes, optionally limited to one block.</summary>
    public IReadOnlyList<LogicNoteDto> Get(Guid? blockId, int limit)
    {
        int take = Math.Clamp(limit <= 0 ? 50 : limit, 1, 200);

        lock (sync_)
        {
            IEnumerable<LogicNoteDto> query = notes_;
            if (blockId is not null)
            {
                query = query.Where(note => note.BlockId == blockId.Value);
            }

            return query
                .OrderByDescending(note => note.CreatedUtc)
                .ThenByDescending(note => note.Id)
                .Take(take)
                .ToArray();
        }
    }

    public bool TryAdd(LogicNoteAddRequest request, string? author, out LogicNoteDto? note, out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.BlockId == Guid.Empty)
        {
            note = null;
            error = "Block is required.";
            return false;
        }

        string text = request.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > 500)
        {
            note = null;
            error = "Note text is required (max 500 characters).";
            return false;
        }

        string resolvedAuthor = author?.Trim() ?? string.Empty;
        if (resolvedAuthor.Length == 0)
        {
            resolvedAuthor = "app";
        }

        note = new LogicNoteDto
        {
            Id = Guid.NewGuid(),
            BlockId = request.BlockId,
            ConditionId = request.ConditionId == Guid.Empty ? null : request.ConditionId,
            Text = text,
            Author = resolvedAuthor,
            CreatedUtc = DateTime.UtcNow
        };

        lock (sync_)
        {
            notes_.Add(note);
            if (notes_.Count > MaxNotes)
            {
                notes_.RemoveRange(0, notes_.Count - MaxNotes);
            }

            Persist();
        }

        error = null;
        return true;
    }

    private void Persist()
    {
        try
        {
            string json = JsonSerializer.Serialize(notes_, JsonOptions);
            File.WriteAllText(persist_path_, json);
        }
        catch
        {
        }
    }

    private List<LogicNoteDto>? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(persist_path_))
            {
                return null;
            }

            string json = File.ReadAllText(persist_path_);
            List<LogicNoteDto>? loaded = JsonSerializer.Deserialize<List<LogicNoteDto>>(json);
            if (loaded is null)
            {
                return null;
            }

            List<LogicNoteDto> sanitized = loaded
                .Where(note => note.BlockId != Guid.Empty && !string.IsNullOrWhiteSpace(note.Text))
                .OrderBy(note => note.CreatedUtc)
                .ToList();

            if (sanitized.Count > MaxNotes)
            {
                sanitized.RemoveRange(0, sanitized.Count - MaxNotes);
            }

            return sanitized;
        }
        catch
        {
            return null;
        }
    }
}
