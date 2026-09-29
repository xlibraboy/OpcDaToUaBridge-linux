using OpcBridge.App;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// Reading a tag list out of an MX OPC Configurator CSV and deciding what each row means
/// (issue #30): the file names a tag, and the bridge says whether that tag is new, already
/// mapped, mapped with a different description, repeated in the file — and, when the source
/// could be read, whether the source really exposes it.
/// </summary>
public sealed class TagImportFileTests
{
    private const string MxExport = """
        #LimitAlarmDefinitions;
        "LocationPath","Name","UpdateRate"

        #MX_DataTags;
        "LocationPath","Name","Description","Enable","AddressAsString","DataTypeAsString"
        "\Address Space\DRYEND_PLC\Input_X","X000","1D Canvas Stretch","Yes","X0","BOOL"
        "\Address Space\DRYEND_PLC\Input_X","X001","","Yes","X1","BOOL"
        "\Address Space\DRYEND_PLC\Output_Y","Y100","Coil, ""main"" drive","Yes","Y100","BOOL"

        #MX_DataTagsRoot;
        "LocationPath","Name"
        "\","Production Line"
        """;

    [Fact]
    public void Parse_ReadsTagNameDescriptionGroupAndQualifiedItemId()
    {
        Assert.True(TagImportFile.TryParseMxOpcTags(MxExport, out List<ImportedTag> tags, out string error), error);

        Assert.Equal(3, tags.Count);
        Assert.Equal("X000", tags[0].Name);
        // The item id is the PLC/folder path plus the name, with MX's own root dropped: three
        // PLCs each carry an X000, and the path is what tells them apart.
        Assert.Equal("DRYEND_PLC.Input_X.X000", tags[0].ItemId);
        Assert.Equal("1D Canvas Stretch", tags[0].Description);
        Assert.Equal(@"\Address Space\DRYEND_PLC\Input_X", tags[0].Group);
        // A blank cell means "no description", not an empty one — an import must never blank out
        // a description someone typed in the bridge.
        Assert.Null(tags[1].Description);
        // Delimiters, quotes and later sections: a quoted description keeps its comma and quotes,
        // and the tag table ends at the next "#Section;" line.
        Assert.Equal("Y100", tags[2].Name);
        Assert.Equal("DRYEND_PLC.Output_Y.Y100", tags[2].ItemId);
        Assert.Equal("Coil, \"main\" drive", tags[2].Description);
        Assert.DoesNotContain(tags, tag => tag.Name == "Production Line");
    }

    [Fact]
    public void Parse_QualifiesTheItemIdWithEveryFolderBelowTheMxRoot()
    {
        // The MX root itself is not part of a tag's path, whatever casing or slashes it uses, and
        // a tag with no folder at all keeps its bare name.
        Assert.Equal("DRYEND_PLC.Input_X.X000", TagImportFile.BuildItemId("X000", @"\Address Space\DRYEND_PLC\Input_X"));
        Assert.Equal("DRYEND_PLC.Input_X.X000", TagImportFile.BuildItemId("X000", "address space/DRYEND_PLC/Input_X/"));
        Assert.Equal("DRYEND_PLC.X000", TagImportFile.BuildItemId("X000", @"\Address Space\DRYEND_PLC"));
        Assert.Equal("X000", TagImportFile.BuildItemId("X000", ""));
        Assert.Equal("X000", TagImportFile.BuildItemId("X000", @"\Address Space"));
    }

    [Fact]
    public void Compare_KeepsSameNamedTagsOfDifferentPlcsApart()
    {
        // The bug this guards (a real 2 715-tag MX export): X000 exists under three PLCs, and
        // deduplicating by bare name struck out 1 574 rows as "repeat in file". Each PLC's X000
        // is its own tag, because its item id carries the path.
        const string threePlcs = """
            #MX_DataTags;
            "LocationPath","Name","Description"
            "\Address Space\DRYEND_PLC\Input_X","X000","Dry end"
            "\Address Space\WETEND_PLC\Input_X","X000","Wet end"
            "\Address Space\WINDERMHI_PLC\Input_X","X000","Winder"
            "\Address Space\WINDERMHI_PLC\Input_X","x000","A true repeat"
            """;

        Assert.True(TagImportFile.TryParseMxOpcTags(threePlcs, out List<ImportedTag> tags, out string error), error);
        Assert.Equal(4, tags.Count);

        List<TagImportRow> rows = TagImportComparer.Compare(
            tags,
            new Dictionary<string, TagMapping>(StringComparer.OrdinalIgnoreCase),
            sourceTags: null);

        Assert.Equal(
            new[] { "DRYEND_PLC.Input_X.X000", "WETEND_PLC.Input_X.X000", "WINDERMHI_PLC.Input_X.X000", "WINDERMHI_PLC.Input_X.x000" },
            rows.Select(row => row.ItemId));
        // Only the genuine repeat — same path, same name, different casing — is flagged.
        Assert.Equal(TagImportStatus.New, rows[0].Status);
        Assert.Equal(TagImportStatus.New, rows[1].Status);
        Assert.Equal(TagImportStatus.New, rows[2].Status);
        Assert.Equal(TagImportStatus.DuplicateInFile, rows[3].Status);
    }

    [Fact]
    public void Parse_WithoutTheTagTable_FailsWithAnActionableReason()
    {
        const string otherFile = """
            Id,Name,Value
            1,Not a tag table,5
            """;

        Assert.False(TagImportFile.TryParseMxOpcTags(otherFile, out List<ImportedTag> tags, out string error));
        Assert.Empty(tags);
        Assert.Contains(TagImportFile.MxOpcTagsSection, error);
    }

    [Fact]
    public void Parse_EmptyTagTable_ReadsZeroTagsRatherThanFailing()
    {
        const string empty = "#MX_DataTags;\r\n\"LocationPath\",\"Name\",\"Description\"\r\n";

        Assert.True(TagImportFile.TryParseMxOpcTags(empty, out List<ImportedTag> tags, out string error), error);
        Assert.Empty(tags);
    }

    [Fact]
    public void Parse_RowsWithoutATagName_AreLeftOut()
    {
        const string withBlank = """
            #MX_DataTags;
            "LocationPath","Name","Description"
            "\Plc\In","","No name here"
            "\Plc\In","X10","Kept"
            """;

        Assert.True(TagImportFile.TryParseMxOpcTags(withBlank, out List<ImportedTag> tags, out string error), error);
        Assert.Equal("X10", Assert.Single(tags).Name);
        Assert.Equal("Plc.In.X10", Assert.Single(tags).ItemId);
    }

    [Fact]
    public void Parse_EmptyOrMissingText_IsRejected()
    {
        Assert.False(TagImportFile.TryParseMxOpcTags(null, out _, out string nullError));
        Assert.False(string.IsNullOrWhiteSpace(nullError));
        Assert.False(TagImportFile.TryParseMxOpcTags("   ", out _, out string blankError));
        Assert.False(string.IsNullOrWhiteSpace(blankError));
    }

    [Fact]
    public void Compare_SeparatesNewMappedDifferingAndRepeatedRows()
    {
        DateTime stamp = new(2026, 9, 29, 3, 15, 0, DateTimeKind.Utc);
        Dictionary<string, TagMapping> mapped = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Plc.In.X000"] = new TagMapping { SourceId = "default", ItemId = "Plc.In.X000", Description = "Kept description", AddedUtc = stamp },
            ["Plc.Out.Y100"] = new TagMapping { SourceId = "default", ItemId = "Plc.Out.Y100", Description = null, AddedUtc = stamp }
        };
        List<ImportedTag> imported = new()
        {
            new ImportedTag("X000", "Plc.In.X000", "Kept description", @"\Plc\In"),
            new ImportedTag("Y100", "Plc.Out.Y100", "Now described", @"\Plc\Out"),
            new ImportedTag("X010", "Plc.In.X010", "New tag", @"\Plc\In"),
            new ImportedTag("x010", "Plc.In.x010", "Repeated tag", @"\Plc\In")
        };
        HashSet<string> source = new(StringComparer.OrdinalIgnoreCase) { "Plc.In.X000", "Plc.In.X010" };

        List<TagImportRow> rows = TagImportComparer.Compare(imported, mapped, source);

        Assert.Equal(TagImportStatus.Mapped, rows[0].Status);
        Assert.Equal(stamp, rows[0].AddedUtc);
        Assert.True(rows[0].OnSource);
        // A stored mapping with no description and a file that has one differs — that is the
        // import's chance to fill it in.
        Assert.Equal(TagImportStatus.DescriptionDiffers, rows[1].Status);
        Assert.Null(rows[1].ExistingDescription);
        // Y100 is not on the source: the row says so instead of looking like an ordinary add.
        Assert.False(rows[1].OnSource);
        Assert.Equal(TagImportStatus.New, rows[2].Status);
        // The file's second X010 is reported as a repeat; item ids match case-insensitively, as
        // the mapping store keys them.
        Assert.Equal(TagImportStatus.DuplicateInFile, rows[3].Status);
        // The row keeps both faces of the tag: the name the operator knows and the id it maps as.
        Assert.Equal("X000", rows[0].Name);
        Assert.Equal("Plc.In.X000", rows[0].ItemId);
    }

    [Fact]
    public void Compare_WithoutASourceRead_LeavesEveryRowUnknown()
    {
        List<ImportedTag> imported = new() { new ImportedTag("X000", "Plc.In.X000", null, @"\Plc\In") };

        List<TagImportRow> rows = TagImportComparer.Compare(
            imported,
            new Dictionary<string, TagMapping>(StringComparer.OrdinalIgnoreCase),
            sourceTags: null);

        Assert.Null(Assert.Single(rows).OnSource);
    }

    [Fact]
    public void Reconcile_ListsTheSourceTagsTheFileDoesNotMention()
    {
        // The comparison runs both ways: the file against the source, and the source's own tags
        // against the file — that second half is what shows a tag the server exposes that the
        // file never lists, and whether the bridge already maps it.
        Dictionary<string, TagMapping> mapped = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Plc.In.X000"] = new TagMapping { SourceId = "default", ItemId = "Plc.In.X000", Description = "Kept" }
        };
        List<ImportedTag> imported = new() { new ImportedTag("X001", "Plc.In.X001", "Rope", @"\Plc\In") };
        List<TagImportRow> rows = TagImportComparer.Compare(imported, mapped, sourceTags: null);
        HashSet<string> source = new(StringComparer.OrdinalIgnoreCase) { "Plc.In.X000", "Plc.In.X001", "Plc.In.X002", "Plc.In.X003" };

        TagImportReconciliation reconciliation = TagImportComparer.Reconcile(rows, mapped, source);

        // X001 is in the file, so three source tags remain — the unmapped ones first.
        Assert.Equal(3, reconciliation.SourceOnlyCount);
        Assert.False(reconciliation.SourceOnlyTruncated);
        Assert.Equal(new[] { "Plc.In.X002", "Plc.In.X003", "Plc.In.X000" }, reconciliation.SourceOnly.Select(tag => tag.ItemId));
        Assert.False(reconciliation.SourceOnly[0].Mapped);
        Assert.Null(reconciliation.SourceOnly[0].Description);
        Assert.True(reconciliation.SourceOnly[2].Mapped);
        Assert.Equal("Kept", reconciliation.SourceOnly[2].Description);
    }

    [Fact]
    public void Reconcile_WithoutASourceRead_KnowsOfNoSourceOnlyTags()
    {
        // A source that could not be read is "nothing known", never "the source has no tags".
        List<TagImportRow> rows = TagImportComparer.Compare(
            new List<ImportedTag> { new ImportedTag("X000", "Plc.In.X000", null, @"\Plc\In") },
            new Dictionary<string, TagMapping>(StringComparer.OrdinalIgnoreCase),
            sourceTags: null);

        TagImportReconciliation reconciliation = TagImportComparer.Reconcile(
            rows,
            new Dictionary<string, TagMapping>(StringComparer.OrdinalIgnoreCase),
            sourceTags: null);

        Assert.Empty(reconciliation.SourceOnly);
        Assert.Equal(0, reconciliation.SourceOnlyCount);
        Assert.False(reconciliation.SourceOnlyTruncated);
    }

    [Fact]
    public void Reconcile_CapsTheListButKeepsTheRealCount()
    {
        int extra = 10;
        HashSet<string> source = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < TagImportComparer.SourceOnlyCap + extra; i++)
        {
            source.Add("Tag" + i.ToString("D5"));
        }

        TagImportReconciliation reconciliation = TagImportComparer.Reconcile(
            new List<TagImportRow>(),
            new Dictionary<string, TagMapping>(StringComparer.OrdinalIgnoreCase),
            source);

        Assert.Equal(TagImportComparer.SourceOnlyCap, reconciliation.SourceOnly.Count);
        Assert.Equal(TagImportComparer.SourceOnlyCap + extra, reconciliation.SourceOnlyCount);
        Assert.True(reconciliation.SourceOnlyTruncated);
    }
}
