using System.Text.Json;
using System.Text.RegularExpressions;
using OpcBridge.Client;

namespace OpcBridge.App.Hmi;

public enum DisplayPutStatus
{
    Ok,
    Conflict,
    Invalid
}

public sealed record DisplayPutResult(
    DisplayPutStatus Status,
    DisplayDocumentDto? Document = null,
    string? Error = null,
    int? CurrentVersion = null);

public sealed class DisplayStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex IdPattern = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    private readonly object sync_ = new();
    private readonly string displays_dir_;

    public DisplayStore()
        : this(DataDirectory.Value)
    {
    }

    public DisplayStore(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        displays_dir_ = Path.Combine(baseDirectory, "displays");
        Directory.CreateDirectory(displays_dir_);
    }

    public IReadOnlyList<DisplayListItemDto> List()
    {
        lock (sync_)
        {
            List<DisplayListItemDto> items = new();
            foreach (string path in Directory.EnumerateFiles(displays_dir_, "*.json"))
            {
                try
                {
                    DisplayDocumentDto? doc = ReadFile(path);
                    if (doc is null || string.IsNullOrWhiteSpace(doc.Id))
                    {
                        continue;
                    }

                    items.Add(ToListItem(doc));
                }
                catch
                {
                    // skip corrupt files
                }
            }

            return items
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(string id, out DisplayDocumentDto? document)
    {
        document = null;
        if (!IsValidId(id))
        {
            return false;
        }

        lock (sync_)
        {
            string path = PathFor(id);
            if (!File.Exists(path))
            {
                return false;
            }

            document = ReadFile(path);
            return document is not null;
        }
    }

    public DisplayPutResult Put(DisplayDocumentDto incoming)
    {
        if (incoming is null)
        {
            return new DisplayPutResult(DisplayPutStatus.Invalid, Error: "body required");
        }

        string id = (incoming.Id ?? string.Empty).Trim();
        if (!IsValidId(id))
        {
            return new DisplayPutResult(DisplayPutStatus.Invalid, Error: "invalid id");
        }

        if (incoming.SchemaVersion != 1)
        {
            return new DisplayPutResult(DisplayPutStatus.Invalid, Error: "unsupported schemaVersion");
        }

        string? validationError = ValidateDocument(incoming);
        if (validationError is not null)
        {
            return new DisplayPutResult(DisplayPutStatus.Invalid, Error: validationError);
        }

        lock (sync_)
        {
            string path = PathFor(id);
            DisplayDocumentDto? existing = File.Exists(path) ? ReadFile(path) : null;

            if (existing is null)
            {
                DisplayDocumentDto created = CloneNormalized(incoming, id, version: 1, DateTime.UtcNow);
                WriteAtomic(path, created);
                return new DisplayPutResult(DisplayPutStatus.Ok, created);
            }

            if (incoming.Version != existing.Version)
            {
                return new DisplayPutResult(
                    DisplayPutStatus.Conflict,
                    Error: "version conflict",
                    CurrentVersion: existing.Version);
            }

            DisplayDocumentDto updated = CloneNormalized(incoming, id, existing.Version + 1, DateTime.UtcNow);
            WriteAtomic(path, updated);
            return new DisplayPutResult(DisplayPutStatus.Ok, updated);
        }
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id))
        {
            return false;
        }

        lock (sync_)
        {
            string path = PathFor(id);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && IdPattern.IsMatch(id.Trim());

    private string PathFor(string id) => Path.Combine(displays_dir_, id.Trim() + ".json");

    private static DisplayDocumentDto? ReadFile(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DisplayDocumentDto>(json, JsonOptions);
    }

    private static void WriteAtomic(string path, DisplayDocumentDto document)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(temp, json);
        File.Copy(temp, path, overwrite: true);
        File.Delete(temp);
    }

    private static string? ValidateDocument(DisplayDocumentDto doc)
    {
        if (doc.Width <= 0 || doc.Height <= 0)
        {
            return "width and height must be positive";
        }

        HashSet<string> widgetIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (DisplayWidgetDto widget in doc.Widgets ?? Enumerable.Empty<DisplayWidgetDto>())
        {
            if (string.IsNullOrWhiteSpace(widget.Id))
            {
                return "widget id required";
            }

            if (string.IsNullOrWhiteSpace(widget.Type))
            {
                return "widget type required";
            }

            if (double.IsNaN(widget.X) || double.IsNaN(widget.Y) || double.IsNaN(widget.W) || double.IsNaN(widget.H)
                || double.IsInfinity(widget.X) || double.IsInfinity(widget.Y)
                || double.IsInfinity(widget.W) || double.IsInfinity(widget.H))
            {
                return "widget bounds invalid";
            }

            if (widget.W < 0 || widget.H < 0)
            {
                return "widget size must be non-negative";
            }

            if (!widgetIds.Add(widget.Id.Trim()))
            {
                return "duplicate widget id";
            }
        }

        return null;
    }

    private static DisplayDocumentDto CloneNormalized(DisplayDocumentDto incoming, string id, int version, DateTime updatedUtc)
    {
        return new DisplayDocumentDto
        {
            SchemaVersion = 1,
            Id = id,
            Name = string.IsNullOrWhiteSpace(incoming.Name) ? id : incoming.Name.Trim(),
            Version = version,
            UpdatedUtc = updatedUtc,
            Width = incoming.Width,
            Height = incoming.Height,
            Widgets = (incoming.Widgets ?? new List<DisplayWidgetDto>()).Select(w => new DisplayWidgetDto
            {
                Id = w.Id.Trim(),
                Type = w.Type.Trim(),
                X = w.X,
                Y = w.Y,
                W = w.W,
                H = w.H,
                Z = w.Z,
                Props = w.Props is null
                    ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    : new Dictionary<string, JsonElement>(w.Props, StringComparer.Ordinal),
                Binding = w.Binding is null
                    ? null
                    : new TagBindingDto
                    {
                        BridgeId = (w.Binding.BridgeId ?? string.Empty).Trim(),
                        SourceId = (w.Binding.SourceId ?? string.Empty).Trim(),
                        DaItemId = (w.Binding.DaItemId ?? string.Empty).Trim()
                    }
            }).ToList()
        };
    }

    private static DisplayListItemDto ToListItem(DisplayDocumentDto doc) => new()
    {
        Id = doc.Id,
        Name = doc.Name,
        Version = doc.Version,
        UpdatedUtc = doc.UpdatedUtc,
        WidgetCount = doc.Widgets?.Count ?? 0
    };
}
