using System.Text.RegularExpressions;
using OpcBridge.App.Auth;
using OpcBridge.Client;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The Logic page (src/OpcBridge.App/Assets/logic) is a second authoring surface for the
/// plant-logic store the Avalonia HMI's Logic page writes, so these tests pin the pieces that
/// would otherwise drift silently: it is served by the bridge, it stays reachable without a
/// session like the dashboard shell, it edits the same endpoints, its element vocabulary is the
/// bridge's own, and it loads nothing from the network (a plant host has no internet).
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class LogicPageTests
{
    private const string HtmlFile = "src/OpcBridge.App/Assets/logic/index.html";
    private const string CssFile = "src/OpcBridge.App/Assets/logic/app.css";
    private const string ScriptFile = "src/OpcBridge.App/Assets/logic/app.js";

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpcBridge.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string OnDisk(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath)).Replace("\r\n", "\n");

    /// <summary>The script between two markers — one region of app.js, for a structural assertion.</summary>
    private static string Between(string script, string from, string to)
    {
        int start = script.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, from + " is not declared in app.js");
        int end = script.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, to + " does not follow " + from + " in app.js");
        return script[start..end];
    }

    private static List<string> StringList(string script, string name)
    {
        Match match = Regex.Match(script, @"const " + name + @" = \[([^\]]*)\]");
        Assert.True(match.Success, name + " is not declared in app.js");
        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('\'', '"'))
            .ToList();
    }

    private static Dictionary<string, int> NumberMap(string script, string name)
    {
        Match match = Regex.Match(script, @"const " + name + @" = \{([^}]*)\}");
        Assert.True(match.Success, name + " is not declared in app.js");
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split(':', StringSplitOptions.TrimEntries);
            Assert.Equal(2, parts.Length);
            map[parts[0].Trim('\'', '"')] = int.Parse(parts[1]);
        }

        return map;
    }

    [Fact]
    public async Task Page_IsServedWithItsAssets()
    {
        await using TestAppHandle app = await TestAppHandle.StartAsync(static _ => { });

        using HttpResponseMessage page = await app.Client.GetAsync("/logic");
        Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        string html = await page.Content.ReadAsStringAsync();
        // Root-relative asset URLs: the page is served at "/logic" with no trailing slash, so a
        // bare "app.css" resolves to "/app.css" and the page arrives unstyled and inert.
        Assert.Contains("href=\"/logic/app.css\"", html);
        Assert.Contains("src=\"/logic/app.js\"", html);
        Assert.DoesNotContain("href=\"app.css\"", html);
        Assert.DoesNotContain("src=\"app.js\"", html);
        Assert.Contains("id=\"blockList\"", html);

        // Trailing slash is what a served directory URL looks like; both land on the page.
        using HttpResponseMessage slashed = await app.Client.GetAsync("/logic/");
        Assert.Equal(System.Net.HttpStatusCode.OK, slashed.StatusCode);

        using HttpResponseMessage script = await app.Client.GetAsync("/logic/app.js");
        Assert.Equal(System.Net.HttpStatusCode.OK, script.StatusCode);
        string js = await script.Content.ReadAsStringAsync();
        Assert.Contains("/api/logic/state", js);

        using HttpResponseMessage css = await app.Client.GetAsync("/logic/app.css");
        Assert.Equal(System.Net.HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void Page_StaysReachableWithoutASession()
    {
        // A page shell, exactly like the dashboard's "/": the data behind it follows the role
        // table, so the paths below must stay public while the API stays gated.
        Assert.True(AuthPolicy.IsPublic("/logic"));
        Assert.True(AuthPolicy.IsPublic("/logic/"));
        Assert.True(AuthPolicy.IsPublic("/logic/app.js"));
        Assert.True(AuthPolicy.IsPublic("/logic/app.css"));

        Assert.False(AuthPolicy.IsPublic("/api/logic"));
        Assert.False(AuthPolicy.IsPublic("/api/logic/blocks"));
    }

    [Fact]
    public void Script_EditsTheSameStoreAsTheHmi()
    {
        string script = OnDisk(ScriptFile);

        Assert.Contains("'/api/logic'", script);
        Assert.Contains("'/api/logic/blocks'", script);
        Assert.Contains("'/api/logic/blocks/'", script);
        Assert.Contains("'/api/logic/state'", script);
        Assert.Contains("method: 'POST'", script);
        Assert.Contains("method: 'DELETE'", script);
        // Saving posts the whole block, the same body the HMI's Save sends.
        Assert.Contains("JSON.stringify({ block: draft })", script);
        // Errors come back as { error } from the store and must reach the operator.
        Assert.Contains("body.error", script);
    }

    [Fact]
    public void Script_CarriesTheHmisThreeTabs()
    {
        string script = OnDisk(ScriptFile);

        // Editor / Tags / Result: the same three surfaces the HMI's Logic page shows.
        Assert.Contains("labels = { editor: 'Editor', tags: 'Tags', result: 'Result' }", script);
        Assert.Contains("role: 'tablist'", script);

        // Tags: the area's shared list, saved and deleted through the endpoints the HMI uses,
        // with the bridge's mapped tags as the source of the picker (never a value column).
        Assert.Contains("'/api/logic/tagsources'", script);
        Assert.Contains("'/api/logic/tagsources/'", script);
        Assert.Contains("JSON.stringify({ tagSource:", script);
        Assert.Contains("'/api/hmi/tags'", script);
        Assert.Contains("From bridge…", script);
        Assert.Contains("tagNameFor(", script);

        // Result: everything the retired Logic web app's result screen carried — the mark, the
        // contact form / function-block pin, the sentence, the bit AND the raw value behind it,
        // the comparison line, timer and counter progress, the tag in display names, what to do
        // next, per-step cards, and the block rows' own state-and-reason line plus the summary.
        Assert.Contains("function renderResultTab(", script);
        Assert.Contains("function kindLabelFor(", script);
        Assert.Contains("function actualBit(", script);
        Assert.Contains("function shouldLine(", script);
        Assert.Contains("'should ' + state.should + ' · actual '", script);
        Assert.Contains("function progressLine(", script);
        Assert.Contains("'holding'", script);
        Assert.Contains("'off-delay'", script);
        Assert.Contains("'pulse'", script);
        Assert.Contains("'count '", script);
        Assert.Contains("function tagLabel(", script);
        Assert.Contains("function sequenceProgress(", script);
        Assert.Contains("function stepAssignments(", script);
        Assert.Contains("function blockWhy(", script);
        Assert.Contains("function blockSummary(", script);
        Assert.Contains("STEP_WORD = { done: 'Done', current: 'Current', pending: 'Pending', unknown: 'No data' }", script);
        // The raw reading sits beside the bit, and the next-step text waits for a false row.
        Assert.Contains("'rl-value mono small'", script);
        Assert.Contains("def.nextStepText && state.state !== 'true'", script);
        Assert.Contains("PIN_LABEL", script);
        Assert.Contains("ton: 'TON IN'", script);
    }

    [Fact]
    public void Script_SplitsEditorAndTagsTheWayTheHmiDoes()
    {
        string script = OnDisk(ScriptFile);

        // The HMI's own split, surface for surface: the Editor authors the block and its rows, and
        // the Tags tab owns the assignment — the block's AREA and the area's list, each named tag
        // bound to its source and tag. A row picks a name; it never assigns a source/tag itself.
        string head = Between(script, "function renderHead(", "function renderAreaState(");
        Assert.DoesNotContain("fArea", head);

        string tags = Between(script, "function renderTagsTab(", "function saveAreaTags(");
        Assert.Contains("id: 'fArea'", tags);
        Assert.Contains("JSON.stringify", script);   // the list is saved through the tagsources API
        // The Tags tab lists the area's tags and nothing else: no per-row section (the rows pick
        // their tag on the Editor tab, the way the HMI's rows do).
        Assert.DoesNotContain("renderBlockRows", tags);
        // The block's own save rides the tab: the area it names is stored with the block.
        Assert.Contains("renderSaveBarNode()", tags);
        // The list being edited is the block's area's, and it follows the name as it is typed.
        Assert.Contains("function syncTagDraft(", tags);
        Assert.Contains("draft.area = e.target.value; markDirty(); refreshTagList();", tags);
        // A list's unsaved edits are kept per area, so stepping to another one drops nothing.
        Assert.Contains("function rememberTagDraft(", tags);

        // The Editor row keeps its TAG field — the area's entries by name plus the relay route —
        // and offers no way to type a pair: the assignment lives on the Tags tab.
        string controls = Between(script, "function renderControls(", "function tagPickSelect(");
        Assert.Contains("tagPickSelect(element, rerender)", controls);
        Assert.DoesNotContain("w-pair", controls);
        // The two raw-pair inputs the row used to carry are gone from the page entirely.
        Assert.DoesNotContain("'aria-label': 'Source id'", script);
        Assert.DoesNotContain("'aria-label': 'Item id'", script);
        Assert.DoesNotContain("'__direct__'", script);
        Assert.Contains("text: '(internal relay)'", script);

        // A row still carrying the legacy pair shows it the HMI's way: read-only, with the whole
        // sentence in the mark's tooltip, and the pair itself kept through a save.
        Assert.Contains("function isStoredPair(", script);
        Assert.Contains("class: 'info-mark'", controls);
        Assert.Contains("class: 'stored-tag mono small'", controls);
        Assert.Contains("'Direct tag: ' + storedTagLabel(element) + ' — add it to this area\\'s tags (the Tags tab) to assign it there.'", script);
    }

    [Fact]
    public void Script_MatchesTheHmisOwnWording()
    {
        string script = OnDisk(ScriptFile);

        // LogicText's words, so the plant reads the same thing on the HMI and on this page:
        // block kinds, the state words that always sit next to a colour, marks, and the row labels.
        Assert.Contains("BLOCK_KIND_LABEL = { interlock: 'Interlock', permissive: 'Permissive', sequence: 'Sequence' }", script);
        Assert.Contains("if (word === 'ready') return 'READY';", script);
        Assert.Contains("if (word === 'blocked') return 'BLOCKED';", script);
        Assert.Contains("if (word === 'disabled') return 'DISABLED';", script);
        Assert.Contains("return 'NO DATA';", script);
        Assert.Contains("state.state === 'true' ? '✓' : state.state === 'false' ? '✗' : '—'", script);
        Assert.Contains("if (kind === 'coil') return 'COIL';", script);

        // LogicText.Actual: the bit a contact reads, flipped for a normally closed one, "?" unknown,
        // and a comparison keeps its own value.
        Assert.Contains("function actualBit(", script);
        Assert.Contains("return String(op === 'on' ? bit : 1 - bit);", script);
        Assert.Contains("return value && value !== '—' ? value : '?';", script);

        // LogicText.Progress / ShouldLine / SequenceProgress.
        Assert.Contains("return 'should ' + state.should + ' · actual ' + actualBit(state, def);", script);
        Assert.Contains("return verb + ' done · ' + secondsText(pt);", script);
        Assert.Contains("return verb + ' ' + secondsText(state.elapsedMs) + ' of ' + secondsText(pt);", script);
        Assert.Contains("return 'count ' + Number(state.count || 0) + ' of ' + Number((def && def.pv) || 0);", script);
        Assert.Contains("return done + '/' + steps.length + ' steps' + (current ? ' · ' + current : '');", script);
        Assert.Contains("if (done === steps.length) return 'complete';", script);

        // LogicText.TagContactLabel / LogicTagCatalog.TagLabel: the plant's names, and the three
        // ways a named entry can fail to resolve.
        Assert.Contains("function catalogLabel(", script);
        Assert.Contains("? label + ' · ' + tag.sourceName.trim() : label;", script);
        Assert.Contains("return name + ' (not assigned)';", script);
        Assert.Contains("return name + (areaName ? ' (not in ' + areaName + ')' : ' (no area)');", script);
        Assert.Contains("return 'relay ' + def.relay.trim();", script);

        // LogicText.TagLiveChip: the Tags tab's raw chip, quality included.
        Assert.Contains("if (tag.isGood === false) return 'bad';", script);
        Assert.Contains("value.toFixed(3).replace(/\\.?0+$/, '')", script);

        // LogicText.Freshness: the feed's own state wins over the snapshot's age.
        Assert.Contains("return 'stale — bridge unreachable';", script);
        Assert.Contains("return 'stale — reconnecting';", script);
        Assert.Contains("return 'no state yet';", script);
        Assert.Contains("return 'updated just now';", script);

        // LogicText.LiveChip and the four empty states.
        Assert.Contains("function liveChip(state)", script);
        Assert.Contains("return state.state === 'true' ? '1' : state.state === 'false' ? '0' : 'no data';", script);
        Assert.Contains("'NO BLOCKS YET'", script);
        Assert.Contains("'NOT SAVED YET'", script);
        Assert.Contains("'NOTHING TO EVALUATE'", script);
        Assert.Contains("'READING…'", script);

        // The Tags tab's notes and the picker's handling of a tag the area already holds.
        Assert.Contains("read-only tag — an action cannot write it.", script);
        Assert.Contains("\"in the area's tags\"", script);
        Assert.Contains("No mapped tags on this bridge — map them in the dashboard", script);
        Assert.Contains("' added to \"' + tagArea + '\" — Save to store them.'", script);
    }

    [Fact]
    public void Script_VocabularyMatchesTheBridge()
    {
        string script = OnDisk(ScriptFile);

        Assert.Equal(LogicElementKinds.All, StringList(script, "KINDS"));
        Assert.Equal(LogicConditionOps.All, StringList(script, "OPS"));
        Assert.Equal(LogicBlockKinds.All, StringList(script, "BLOCK_KINDS"));
        Assert.Equal(LogicConditionSeverities.All, StringList(script, "SEVERITIES"));

        // Input caps are the store's own per-kind limits: an element may not be given more
        // inputs than LogicElementKinds.InputRange allows, or the save is refused.
        Dictionary<string, int> inputMax = NumberMap(script, "INPUT_MAX");
        Assert.Equal(LogicElementKinds.All.Length, inputMax.Count);
        foreach (string kind in LogicElementKinds.All)
        {
            Assert.True(inputMax.ContainsKey(kind), kind + " is missing from INPUT_MAX");
            Assert.Equal(LogicElementKinds.InputRange(kind).Max, inputMax[kind]);
        }

        Dictionary<string, int> limits = NumberMap(script, "LIMITS");
        Assert.Equal(LogicLimits.MaxElementDepth, limits["depth"]);
        Assert.Equal(LogicLimits.MaxElementsPerBlock, limits["elements"]);

        // A compound with no label would render as a raw kind string.
        foreach (string kind in LogicElementKinds.All)
        {
            Assert.Contains(kind + ':', script);
            Assert.Matches(new Regex("\\b" + kind + ": '"), script);
        }
    }

    [Fact]
    public void Page_ThemeSwitcherOffersLightDarkAndSystem()
    {
        string html = OnDisk(HtmlFile);
        string script = OnDisk(ScriptFile);

        // Three preferences with visible labels; "system" follows the operating system. The key
        // is the dashboard's, so a choice made on either surface is the other's choice.
        Assert.Contains("role=\"group\" aria-label=\"Color theme\"", html);
        Assert.Contains("name=\"theme\" value=\"light\"", html);
        Assert.Contains("name=\"theme\" value=\"dark\"", html);
        Assert.Contains("name=\"theme\" value=\"system\"", html);
        // The head resolves the stored choice before first paint, so dark never flashes light.
        Assert.Contains("opcbridge.theme", html);
        Assert.Contains("prefers-color-scheme: dark", html);
        Assert.Contains("const THEME_KEY = 'opcbridge.theme';", script);
        Assert.Contains("function applyTheme(", script);
        Assert.Contains("function initTheme(", script);
        // A system choice follows the OS while the page is open, not only at load.
        Assert.Contains("systemTheme.addEventListener('change', followSystem)", script);
    }

    [Fact]
    public void StyleSheet_DefinesEveryColourInBothThemes()
    {
        string css = OnDisk(CssFile);

        static HashSet<string> Colours(string block) =>
            Regex.Matches(block, @"(--[a-z0-9-]+)\s*:\s*(?:#|rgba?\()")
                .Select(match => match.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

        Match light = Regex.Match(css, @":root \{(.*?)\n\}", RegexOptions.Singleline);
        Match dark = Regex.Match(css, @":root\[data-theme=""dark""\] \{(.*?)\n\}", RegexOptions.Singleline);
        Assert.True(light.Success, ":root carries no token block");
        Assert.True(dark.Success, "the dark token block is missing");
        Assert.Contains("color-scheme: dark;", dark.Value);

        // Dark redefines the same names: a token only the light block carries would keep its
        // light value on a dark ground.
        HashSet<string> lightColours = Colours(light.Groups[1].Value);
        HashSet<string> darkColours = Colours(dark.Groups[1].Value);
        Assert.True(
            lightColours.SetEquals(darkColours),
            "colour tokens differ between the themes — light only: "
            + string.Join(", ", lightColours.Except(darkColours))
            + "; dark only: " + string.Join(", ", darkColours.Except(lightColours)));
    }

    [Fact]
    public void StyleSheet_PinsTheEditorChromeButNotItsTabs()
    {
        string css = OnDisk(CssFile);

        // One selector's own block: the declaration list between its opening brace and the first
        // close, which reaches every flat rule this test reads.
        string Rule(string selector)
        {
            int start = css.IndexOf(selector, StringComparison.Ordinal);
            Assert.True(start >= 0, selector + " is not declared in app.css");
            int end = css.IndexOf('}', start);
            Assert.True(end > start, selector + " is never closed");
            return css[start..end];
        }

        // The editor is the page's own scroll region (body is 100dvh and clipped), so the block's
        // fields and its Save / Revert / Delete have to ride that scroll: a network longer than the
        // window would otherwise carry both away, and the page's save would live at the bottom of a
        // page the operator may never reach. The padding is read back by name: a sticky offset
        // measures from the editor's content box, so the pinned pair cancels it to reach the edges.
        string editor = Rule(".editor {");
        Assert.Contains("--pad-top: 14px;", editor);
        Assert.Contains("--pad-bottom: 40px;", editor);

        string head = Rule(".editor > .ed-head {");
        Assert.Contains("position: sticky", head);
        Assert.Contains("top: calc(-1 * var(--pad-top));", head);

        string bar = Rule(".editor > .savebar {");
        Assert.Contains("position: sticky", bar);
        Assert.Contains("bottom: calc(-1 * var(--pad-bottom));", bar);
        // An opaque ground in the editor's own colour, or the rows would read through the bar.
        Assert.Contains("background: var(--bg);", bar);

        // The tabs stay in the flow by design — they leave with the rows as the network is scrolled
        // and are back once the editor is scrolled to the top again.
        Assert.DoesNotContain("position: sticky", Rule(".tabs {"));
    }

    [Theory]
    [InlineData(HtmlFile)]
    [InlineData(CssFile)]
    [InlineData(ScriptFile)]
    public void Page_LoadsNothingFromTheNetwork(string relativePath)
    {
        string text = OnDisk(relativePath);

        Assert.DoesNotContain("http://", text);
        Assert.DoesNotContain("https://", text);
        Assert.DoesNotContain("//cdn", text);
        Assert.DoesNotContain("integrity=", text);
        Assert.DoesNotContain("@import", text);
    }
}
