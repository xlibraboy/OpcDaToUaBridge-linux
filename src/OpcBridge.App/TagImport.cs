using OpcBridge.Core;

namespace OpcBridge.App;

/// <summary>
/// One tag read out of an import file: the source-side tag name and the description that
/// travels with it. Everything else such exports carry — device address, data type, access
/// rights, poll rate — belongs to the exporting tool, not to the bridge, and stays behind
/// (issue #30: "only get the tags and the description name").
/// </summary>
public sealed record ImportedTag(string ItemId, string? Description, string Group);

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

            string itemId = Field(record, nameIndex).Trim();
            if (itemId.Length == 0)
            {
                continue;
            }

            string description = Field(record, descriptionIndex).Trim();
            tags.Add(new ImportedTag(
                itemId,
                description.Length == 0 ? null : description,
                Field(record, groupIndex).Trim()));
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
/// read — whether the server really exposes it.
/// </summary>
public sealed record TagImportRow(
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
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<TagImportRow> rows = new(imported.Count);

        foreach (ImportedTag tag in imported)
        {
            bool? onSource = sourceTags is null ? null : sourceTags.Contains(tag.ItemId);

            if (!seen.Add(tag.ItemId))
            {
                rows.Add(new TagImportRow(
                    tag.ItemId, tag.Description, tag.Group, TagImportStatus.DuplicateInFile, null, null, onSource));
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
                    tag.ItemId,
                    tag.Description,
                    tag.Group,
                    differs ? TagImportStatus.DescriptionDiffers : TagImportStatus.Mapped,
                    stored,
                    existing.AddedUtc,
                    onSource));
                continue;
            }

            rows.Add(new TagImportRow(tag.ItemId, tag.Description, tag.Group, TagImportStatus.New, null, null, onSource));
        }

        return rows;
    }
}
