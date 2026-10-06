using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// One tag read out of an import file: the tag's own name, the source-side item id it is
/// addressed by, and the description that travels with it. Everything else such exports carry —
/// device address, data type, access rights, poll rate — belongs to the exporting tool, not to
/// the bridge, and stays behind (issue #30: "only get the tags and the description name").
/// <see cref="Name"/> and <see cref="ItemId"/> differ because an MX export names a tag inside a
/// folder: three PLCs each carry an X000, so the folder path is what makes the item id unique
/// (see <see cref="TagImportFile.BuildItemId"/>).
/// </summary>
public sealed record ImportedTag(string Name, string ItemId, string? Description, string Group);

/// <summary>
/// The name an imported tag is mapped with (issue #40): a template the operator edits in the
/// import dialog, with <c>{itemId}</c>, <c>{name}</c>, <c>{description}</c> and <c>{group}</c>
/// tokens. The default is the item id — the path-qualified id is what tells three PLCs' X000
/// apart (see <see cref="TagImportFile.BuildItemId"/>), so it is the one name an import always
/// has. A blank template means the default; a token that is not one of the four is left exactly
/// as typed (it shows up in the preview, where it can be fixed); and a rendering that trims to
/// nothing falls back to the item id, the same fallback <c>MappingStore</c> applies to a blank
/// display name.
/// </summary>
public static class TagNameTemplate
{
    /// <summary>What the dialog starts with, and what an empty box means: the item id.</summary>
    public const string Default = "{itemId}";

    public static string Apply(string? template, string name, string itemId, string? description, string group)
    {
        string text = string.IsNullOrWhiteSpace(template) ? Default : template.Trim();
        System.Text.StringBuilder rendered = new(text.Length + 16);

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
            {
                rendered.Append(text[i]);
                continue;
            }

            int end = text.IndexOf('}', i + 1);
            if (end < 0)
            {
                rendered.Append(text[i]);
                continue;
            }

            // Tokens are matched case-insensitively: the operator may type {ItemId} or {itemId}.
            string? value = text.Substring(i + 1, end - i - 1).Trim().ToLowerInvariant() switch
            {
                "itemid" => itemId,
                "name" => name,
                "description" => description ?? string.Empty,
                "group" => group,
                _ => null
            };

            if (value is null)
            {
                // Not a token we know: keep it as typed, so the preview shows the mistake.
                rendered.Append(text, i, end - i + 1);
            }
            else
            {
                rendered.Append(value);
            }

            i = end;
        }

        string result = rendered.ToString().Trim();
        return result.Length == 0 ? itemId : result;
    }
}

/// <summary>
/// Readers for the tag lists the Maps tab can import. Today that is the CSV written by
/// MELSOFT MX OPC Configurator, whose tag table is the <c>#MX_DataTags</c> section: a header
/// row naming the columns, then one quoted row per tag. Only the columns that name a tag and
/// describe it are read (<c>Name</c>, <c>Description</c>, plus <c>LocationPath</c> to keep the
/// PLC/folder grouping the operator picked in the dialog); the MX file is a list of OPC DA tag
/// names for this bridge, so nothing in it addresses a device directly.
/// </summary>
public static class TagImportFile
{
    /// <summary>MX OPC Configurator's tag table, the only section this reader looks at.</summary>
    public const string MxOpcTagsSection = "#MX_DataTags";

    private const string NameColumn = "Name";
    private const string DescriptionColumn = "Description";
    private const string GroupColumn = "LocationPath";

    /// <summary>MX's own root folder, which is not part of a tag's OPC path.</summary>
    private const string AddressSpaceRoot = "Address Space";

    /// <summary>
    /// The OPC item id a tag is addressed by: its folder path below MX's root, then the tag name,
    /// joined with '.' — an MX export keeps its <c>\Address Space\DRYEND_PLC\Input_X</c> grouping
    /// separate from the tag name, so <c>X000</c> under that folder becomes
    /// <c>DRYEND_PLC.Input_X.X000</c>. The path is what keeps the same-named tags of several PLCs
    /// apart: every PLC in the plant carries its own X000, and a bare name would make them one
    /// mapping. A tag with no usable path keeps its bare name.
    /// </summary>
    public static string BuildItemId(string name, string locationPath)
    {
        List<string> segments = new();
        foreach (string part in (locationPath ?? string.Empty).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = part.Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            // The leading "Address Space" is MX's root, not a PLC or folder.
            if (segments.Count == 0 && string.Equals(segment, AddressSpaceRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            segments.Add(segment);
        }

        string trimmedName = (name ?? string.Empty).Trim();
        return segments.Count == 0 ? trimmedName : string.Join('.', segments) + '.' + trimmedName;
    }

    /// <summary>
    /// Reads the tag table of an MX OPC Configurator CSV. Returns false, with a reason the
    /// operator can act on, when the file is not that export at all; a file whose tag table is
    /// empty reads as zero tags rather than as an error.
    /// </summary>
    public static bool TryParseMxOpcTags(string? text, out List<ImportedTag> tags, out string error)
    {
        tags = new List<ImportedTag>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The file is empty.";
            return false;
        }

        List<List<string>> records = SplitCsv(text);
        int section = FindSection(records);
        if (section < 0)
        {
            error = $"No '{MxOpcTagsSection}' tag table in the file. Export the tags from MX OPC Configurator with File \u25b8 Save As, and pick CSV.";
            return false;
        }

        int headerIndex = section + 1;
        if (headerIndex >= records.Count)
        {
            return true;
        }

        Dictionary<string, int> columns = MapColumns(records[headerIndex]);
        if (!columns.TryGetValue(NameColumn, out int nameIndex))
        {
            error = $"The '{MxOpcTagsSection}' table has no {NameColumn} column, so the file carries no tag names.";
            return false;
        }

        int descriptionIndex = columns.TryGetValue(DescriptionColumn, out int d) ? d : -1;
        int groupIndex = columns.TryGetValue(GroupColumn, out int g) ? g : -1;

        for (int i = headerIndex + 1; i < records.Count; i++)
        {
            List<string> record = records[i];
            if (record.Count == 0 || (record.Count == 1 && record[0].Length == 0))
            {
                continue;
            }

            // The next "#Section;" line ends the tag table.
            if (record[0].StartsWith('#'))
            {
                break;
            }

            string name = Field(record, nameIndex).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            string description = Field(record, descriptionIndex).Trim();
            string group = Field(record, groupIndex).Trim();
            tags.Add(new ImportedTag(
                name,
                BuildItemId(name, group),
                description.Length == 0 ? null : description,
                group));
        }

        return true;
    }

    private static int FindSection(List<List<string>> records)
    {
        for (int i = 0; i < records.Count; i++)
        {
            List<string> record = records[i];
            if (record.Count == 0)
            {
                continue;
            }

            // The marker line is a single field ending in ';' ("#MX_DataTags;").
            string marker = record[0].Trim().TrimEnd(';');
            if (string.Equals(marker, MxOpcTagsSection, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static Dictionary<string, int> MapColumns(List<string> header)
    {
        Dictionary<string, int> columns = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Count; i++)
        {
            string name = header[i].Trim();
            if (name.Length > 0)
            {
                columns.TryAdd(name, i);
            }
        }

        return columns;
    }

    private static string Field(List<string> record, int index) =>
        index >= 0 && index < record.Count ? record[index] : string.Empty;

    /// <summary>
    /// Splits the whole file into records. Quoted fields may contain the delimiter, doubled
    /// quotes and line breaks; the export quotes every field of the tag table, and a
    /// description with a comma in it must not split a row.
    /// </summary>
    private static List<List<string>> SplitCsv(string text)
    {
        List<List<string>> records = new();
        List<string> fields = new();
        System.Text.StringBuilder field = new();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add(fields);
                    fields = new List<string>();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields);
        }

        return records;
    }
}

/// <summary>Where an imported tag stands against the source and against the bridge's mappings.</summary>
public static class TagImportStatus
{
    /// <summary>Not mapped yet — the row an Add would create.</summary>
    public const string New = "new";

    /// <summary>Already mapped, and the file has nothing to add to it.</summary>
    public const string Mapped = "mapped";

    /// <summary>Already mapped, with a different description in the file.</summary>
    public const string DescriptionDiffers = "differs";

    /// <summary>The file lists this tag more than once; only the first row is kept.</summary>
    public const string DuplicateInFile = "duplicate";
}

/// <summary>
/// One row of the import preview: the file's tag, where it stands, and — when the source was
/// read — whether the server really exposes it. <see cref="Name"/> is the tag's own name
/// ("X000"), <see cref="ItemId"/> the path-qualified id it maps as
/// ("DRYEND_PLC.Input_X.X000"); the row is keyed by the item id, which is what the source
/// comparison and the mapping store both use.
/// </summary>
public sealed record TagImportRow(
    string Name,
    string ItemId,
    string? Description,
    string Group,
    string Status,
    string? ExistingDescription,
    DateTime? AddedUtc,
    bool? OnSource);

/// <summary>
/// The tags a source really exposes, read for the import comparison — or why they could not be
/// read. <see cref="Tags"/> null means the comparison fell back to the stored mappings alone.
/// </summary>
public sealed record SourceTagCheck(IReadOnlySet<string>? Tags, bool Truncated, string? Error);

/// <summary>
/// A tag the source exposes that the file does not mention — the other half of the comparison.
/// <see cref="Mapped"/> says whether the bridge already maps it, so the dialog can show what the
/// source offers that the file does not cover.
/// </summary>
public sealed record SourceOnlyTag(string ItemId, bool Mapped, string? Description);

/// <summary>
/// The reconciliation's reverse direction: the source's tags the file leaves out, capped for the
/// wire with the real total kept alongside.
/// </summary>
public sealed record TagImportReconciliation(
    IReadOnlyList<SourceOnlyTag> SourceOnly,
    int SourceOnlyCount,
    bool SourceOnlyTruncated);

/// <summary>
/// Compares an imported tag list with what the bridge already has and with the tags the source
/// actually exposes. Pure so the comparison can be tested without a server (issue #30).
/// </summary>
public static class TagImportComparer
{
    /// <summary>
    /// <paramref name="mapped"/> is keyed by item id (case-insensitively, as the store keys
    /// mappings). <paramref name="sourceTags"/> is what the source browse returned, or null when
    /// the source could not be read — a row then reports <see cref="TagImportRow.OnSource"/> as
    /// null, never as "missing", so an unavailable browse cannot condemn a tag.
    /// </summary>
    public static List<TagImportRow> Compare(
        IReadOnlyList<ImportedTag> imported,
        IReadOnlyDictionary<string, TagMapping> mapped,
        IReadOnlySet<string>? sourceTags)
    {
        // Deduplicated by item id — which, for an MX export, is the PLC/folder path plus the tag
        // name. Reviewing one PLC at a time must not strike out another PLC's X000: only a true
        // repeat of the same path and name is a repeat in the file.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<TagImportRow> rows = new(imported.Count);

        foreach (ImportedTag tag in imported)
        {
            bool? onSource = sourceTags is null ? null : sourceTags.Contains(tag.ItemId);

            if (!seen.Add(tag.ItemId))
            {
                rows.Add(new TagImportRow(
                    tag.Name, tag.ItemId, tag.Description, tag.Group, TagImportStatus.DuplicateInFile, null, null, onSource));
                continue;
            }

            if (mapped.TryGetValue(tag.ItemId, out TagMapping? existing))
            {
                string? stored = string.IsNullOrWhiteSpace(existing.Description) ? null : existing.Description;
                // A file row with no description has no opinion about the stored one: the
                // source export has plenty of blank cells, and importing one must not wipe
                // a description someone typed in the bridge.
                bool differs = tag.Description is not null
                    && !string.Equals(stored, tag.Description, StringComparison.Ordinal);
                rows.Add(new TagImportRow(
                    tag.Name,
                    tag.ItemId,
                    tag.Description,
                    tag.Group,
                    differs ? TagImportStatus.DescriptionDiffers : TagImportStatus.Mapped,
                    stored,
                    existing.AddedUtc,
                    onSource));
                continue;
            }

            rows.Add(new TagImportRow(tag.Name, tag.ItemId, tag.Description, tag.Group, TagImportStatus.New, null, null, onSource));
        }

        return rows;
    }

    /// <summary>
    /// How many source-only rows travel to the dialog. A source can expose tens of thousands of
    /// tags, so the dialog lists what fits and reports the rest by count.
    /// </summary>
    public const int SourceOnlyCap = 500;

    /// <summary>
    /// The tags the source exposes that the file does not mention, each with its mapped state —
    /// the file read the same way the Tag Browser reads it, so the dialog can show what the
    /// server offers beyond the file. <paramref name="sourceTags"/> null means the source could
    /// not be read, which is reported as "nothing known", never as "the source has no tags".
    /// Unmapped tags come first: those are what a reconciliation is for.
    /// </summary>
    public static TagImportReconciliation Reconcile(
        IReadOnlyList<TagImportRow> rows,
        IReadOnlyDictionary<string, TagMapping> mapped,
        IReadOnlySet<string>? sourceTags)
    {
        if (sourceTags is null || sourceTags.Count == 0)
        {
            return new TagImportReconciliation(Array.Empty<SourceOnlyTag>(), 0, SourceOnlyTruncated: false);
        }

        HashSet<string> inFile = new(StringComparer.OrdinalIgnoreCase);
        foreach (TagImportRow row in rows)
        {
            inFile.Add(row.ItemId);
        }

        List<string> missing = new();
        foreach (string itemId in sourceTags)
        {
            if (!inFile.Contains(itemId))
            {
                missing.Add(itemId);
            }
        }

        missing.Sort((left, right) =>
        {
            bool leftMapped = mapped.ContainsKey(left);
            bool rightMapped = mapped.ContainsKey(right);
            return leftMapped == rightMapped
                ? string.Compare(left, right, StringComparison.OrdinalIgnoreCase)
                : (leftMapped ? 1 : -1);
        });

        List<SourceOnlyTag> sourceOnly = new(Math.Min(missing.Count, SourceOnlyCap));
        foreach (string itemId in missing)
        {
            if (sourceOnly.Count == SourceOnlyCap)
            {
                break;
            }

            mapped.TryGetValue(itemId, out TagMapping? tag);
            sourceOnly.Add(new SourceOnlyTag(
                itemId,
                tag is not null,
                tag is null || string.IsNullOrWhiteSpace(tag.Description) ? null : tag.Description));
        }

        return new TagImportReconciliation(sourceOnly, missing.Count, missing.Count > sourceOnly.Count);
    }
}
