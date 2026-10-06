using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The name an imported tag is mapped with (issue #40): a template the operator edits in the
/// import dialog, rendered by the bridge so the preview and the Add that follows cannot disagree.
/// The default is the item id; {name}, {description} and {group} let the operator build a name
/// out of what the file carries, and anything the template cannot mean is kept visible rather
/// than silently dropped.
/// </summary>
public sealed class TagNameTemplateTests
{
    private const string Name = "X000";
    private const string ItemId = "DRYEND_PLC.Input_X.X000";
    private const string Group = @"\Address Space\DRYEND_PLC\Input_X";

    [Fact]
    public void Apply_WithoutATemplate_UsesTheItemId()
    {
        Assert.Equal(ItemId, TagNameTemplate.Apply(null, Name, ItemId, "Stretch", Group));
        Assert.Equal(ItemId, TagNameTemplate.Apply("", Name, ItemId, "Stretch", Group));
        Assert.Equal(ItemId, TagNameTemplate.Apply("   ", Name, ItemId, "Stretch", Group));
    }

    [Fact]
    public void Apply_SubstitutesEveryToken()
    {
        Assert.Equal(
            @"\Address Space\DRYEND_PLC\Input_X / X000 — Stretch",
            TagNameTemplate.Apply("{group} / {name} — {description}", Name, ItemId, "Stretch", Group));
        Assert.Equal(ItemId, TagNameTemplate.Apply("{itemId}", Name, ItemId, "Stretch", Group));
        // A tag with no description leaves the hole empty; it does not print a placeholder.
        Assert.Equal("DE", TagNameTemplate.Apply("D{description}E", Name, ItemId, null, Group));
    }

    [Fact]
    public void Apply_IsCaseInsensitiveOnTokens()
    {
        Assert.Equal(ItemId, TagNameTemplate.Apply("{ITEMID}", Name, ItemId, null, Group));
        Assert.Equal(ItemId, TagNameTemplate.Apply("{ItemId}", Name, ItemId, null, Group));
        Assert.Equal("X000", TagNameTemplate.Apply("{Name}", Name, ItemId, null, Group));
    }

    [Fact]
    public void Apply_KeepsAnUnknownTokenAsTyped()
    {
        // The operator sees the mistake in the preview instead of a name that silently lost it.
        Assert.Equal("T_{oops}_X000", TagNameTemplate.Apply("T_{oops}_{name}", Name, ItemId, null, Group));
        Assert.Equal("half {", TagNameTemplate.Apply("half {", Name, ItemId, null, Group));
    }

    [Fact]
    public void Apply_WhenTheRenderingIsBlank_FallsBackToTheItemId()
    {
        // The same fallback the mapping store applies to a blank display name.
        Assert.Equal(ItemId, TagNameTemplate.Apply("{description}", Name, ItemId, null, Group));
        Assert.Equal(ItemId, TagNameTemplate.Apply("  {description}  ", Name, ItemId, "   ", Group));
    }

    [Fact]
    public void Apply_TrimsTheResult()
    {
        Assert.Equal("X000", TagNameTemplate.Apply("  {name}  ", Name, ItemId, null, Group));
    }
}
