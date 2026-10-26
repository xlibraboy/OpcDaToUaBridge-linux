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

    /// <summary>One selector's own block in app.css: the declarations between its brace and the first close.</summary>
    private static string Rule(string css, string selector)
    {
        int start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, selector + " is not declared in app.css");
        int end = css.IndexOf('}', start);
        Assert.True(end > start, selector + " is never closed");
        return css[start..end];
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
        Assert.Contains("src=\"/logic/signalr.min.js\"", html);
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

        using HttpResponseMessage client = await app.Client.GetAsync("/logic/signalr.min.js");
        Assert.Equal(System.Net.HttpStatusCode.OK, client.StatusCode);
        Assert.Equal("text/javascript", client.Content.Headers.ContentType!.MediaType);
        string clientScript = await client.Content.ReadAsStringAsync();
        // The official browser build, at the version the page was written against, with its MIT
        // notice travelling beside it — a plant host has no internet to fetch either.
        Assert.Contains("ASP.NET Core SignalR JavaScript client 8.0.7", clientScript);
        Assert.Contains("Licensed under the MIT License", clientScript);
        Assert.Contains("HubConnectionBuilder", clientScript);
        // The map is not shipped, so the directive must not ask the browser for it.
        Assert.DoesNotContain("//# sourceMappingURL=", clientScript);
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
        Assert.True(AuthPolicy.IsPublic("/logic/signalr.min.js"));

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

        // One output tag per block, named in the Editor header; the page authors no relays.
        Assert.Contains("id: 'fOutput'", script);
        Assert.Contains("draft.outputTag", script);
        Assert.Contains("function refreshOutputLive(", script);
        Assert.Contains("function displayWord(", script);
        Assert.Contains("function outputLine(", script);
        Assert.Contains("add(block.outputTag);", script);
        Assert.DoesNotContain("text: '(internal relay)'", script);

        // A new name is authored on the row itself. It joins the area's list unassigned, so the
        // Tags tab still owns the one assignment — the source and the tag behind that name.
        Assert.Contains("text: '+ New tag…'", script);
        Assert.Contains("function newTagInput(", script);
        Assert.Contains("function rememberTagName(", script);
        Assert.Contains("function referencedTagNames(", script);
        Assert.Contains("function mergeReferencedTagNames(", script);
        // A new block asks for its AREA, so its rows have a list to name tags into from the start.
        string form = Between(script, "function newBlockForm(", "\nconst val = ");
        Assert.Contains("id: 'nbArea'", form);
        Assert.Contains("newBlock(name, val('nbKind'), val('nbArea').trim(), val('nbGroup').trim())", form);

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

        // The words the HMI's retired Avalonia editor used, so the plant reads the same thing:
        // block kinds, the state words that always sit next to a colour, marks, and the row labels.
        Assert.Contains("BLOCK_KIND_LABEL = { interlock: 'Interlock', permissive: 'Permissive', sequence: 'Sequence' }", script);
        Assert.Contains("if (word === 'ready') return 'READY';", script);
        Assert.Contains("if (word === 'blocked') return 'BLOCKED';", script);
        Assert.Contains("if (word === 'disabled') return 'DISABLED';", script);
        Assert.Contains("return 'NO DATA';", script);
        Assert.Contains("state.state === 'true' ? '✓' : state.state === 'false' ? '✗' : '—'", script);
        Assert.Contains("if (kind === 'coil') return 'COIL';", script);

        // The Editor's network section carries the HMI's own section name, IEC number and all
        // (the page's .section-title is uppercased in the stylesheet, so it is written as a name);
        // the count stays its tail, and a flat-conditions block's read-only expansion is named the
        // same way rather than under a second name for the same thing.
        Assert.Contains("text: 'Network (IEC 61131-3) · '", script);
        Assert.Contains("text: 'Network (IEC 61131-3) — read-only'", script);

        // The actual bit a contact reads, flipped for a normally closed one, "?" unknown, and a
        // comparison keeps its own value.
        Assert.Contains("function actualBit(", script);
        Assert.Contains("return String(op === 'on' ? bit : 1 - bit);", script);
        Assert.Contains("return value && value !== '—' ? value : '?';", script);

        // The timer/counter progress, should-vs-actual line and sequence progress.
        Assert.Contains("return 'should ' + state.should + ' · actual ' + actualBit(state, def);", script);
        Assert.Contains("return verb + ' done · ' + secondsText(pt);", script);
        Assert.Contains("return verb + ' ' + secondsText(state.elapsedMs) + ' of ' + secondsText(pt);", script);
        Assert.Contains("return 'count ' + Number(state.count || 0) + ' of ' + Number((def && def.pv) || 0);", script);
        Assert.Contains("return done + '/' + steps.length + ' steps' + (current ? ' · ' + current : '');", script);
        Assert.Contains("if (done === steps.length) return 'complete';", script);

        // The plant's names, and the three ways a named entry can fail to resolve.
        Assert.Contains("function catalogLabel(", script);
        Assert.Contains("? label + ' · ' + tag.sourceName.trim() : label;", script);
        Assert.Contains("return name + ' (not assigned)';", script);
        Assert.Contains("return name + (areaName ? ' (not in ' + areaName + ')' : ' (no area)');", script);
        Assert.Contains("return 'internal relay ' + def.relay.trim();", script);

        // The Tags tab's raw chip, quality included.
        Assert.Contains("if (tag.isGood === false) return 'bad';", script);
        Assert.Contains("value.toFixed(3).replace(/\\.?0+$/, '')", script);

        // The feed's own state wins over the snapshot's age.
        Assert.Contains("return 'stale — bridge unreachable';", script);
        Assert.Contains("return 'stale — reconnecting';", script);
        Assert.Contains("return 'no state yet';", script);
        Assert.Contains("return 'updated just now';", script);

        // The live chip and the four empty states.
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
    public void Script_ReadsTheExternalDeviceContactFormWithoutChangingTheVerdict()
    {
        string script = OnDisk(ScriptFile);

        // The external device's form is the bridge's own vocabulary and is recorded on the area
        // tag, beside the source and tag — not on each interlock row, where it would drift per block.
        Assert.Equal(LogicContactForms.All, StringList(script, "CONTACT_FORMS"));
        Assert.Contains("const CONTACT_LABEL = { no: 'NO', nc: 'NC' };", script);
        string row = Between(script, "function tagEntryRow(", "function renderTagsTab(");
        Assert.Contains("'aria-label': 'Device contact'", row);
        Assert.Contains("CONTACT_FORMS.map", row);
        Assert.Contains("'data-field-tag'", row);

        // NO is actuated at 1, NC at 0 — the page's one derivation, used by the row, the Result
        // card and the tag inspector.
        Assert.Contains("const actuated = form === 'nc' ? !bit : bit;", script);
        Assert.Contains("'device ' + CONTACT_LABEL[form] + ' · actuated when '", script);
        Assert.Contains("function fieldLine(state, def)", script);
        Assert.Contains("function fieldState(form, bit)", script);

        // The interlock verdict stays the tag's: the field line is added beside should/actual and
        // never writes the compared reading.
        string live = Between(script, "function paintLive(", "function renderControls(");
        Assert.Contains("fieldLine(state, def)", live);
        Assert.Contains("shouldLine(state, def)", live);
        Assert.DoesNotContain("state.matches = ", live);

        string result = Between(script, "function resultRow(", "const STEP_WORD");
        Assert.Contains("fieldLine(state, def)", result);
        Assert.Contains("class: 'rl-line rl-field muted'", result);
    }

    [Fact]
    public void Script_ReadsDerivedWrappersFromTheState()
    {
        string script = OnDisk(ScriptFile);
        string result = Between(script, "function resultRow(", "const STEP_WORD");

        // The bridge expands a block's flat conditions into wrappers (a TON around a held contact,
        // an OR group, the AND over the roots). Those wrappers have no authored definition, so the
        // state carries the definition fields and the row falls back to it.
        Assert.Contains("def = def || state;", result);
        Assert.Contains("def.text", result);
        Assert.Contains("def.severity", result);
        Assert.Contains("def.nextStepText", result);
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

        // The editor is the page's own scroll region (body is 100dvh and clipped), so the block's
        // fields and its Save / Revert / Delete have to ride that scroll: a network longer than the
        // window would otherwise carry both away, and the page's save would live at the bottom of a
        // page the operator may never reach. The padding is read back by name: a sticky offset
        // measures from the editor's content box, so the pinned pair cancels it to reach the edges.
        string editor = Rule(css, ".editor {");
        Assert.Contains("--pad-top: 14px;", editor);
        Assert.Contains("--pad-bottom: 40px;", editor);

        string head = Rule(css, ".editor > .ed-head {");
        Assert.Contains("position: sticky", head);
        Assert.Contains("top: calc(-1 * var(--pad-top));", head);

        string bar = Rule(css, ".editor > .savebar {");
        Assert.Contains("position: sticky", bar);
        Assert.Contains("bottom: calc(-1 * var(--pad-bottom));", bar);
        // An opaque ground in the editor's own colour, or the rows would read through the bar.
        Assert.Contains("background: var(--bg);", bar);

        // The tabs stay in the flow by design — they leave with the rows as the network is scrolled
        // and are back once the editor is scrolled to the top again.
        Assert.DoesNotContain("position: sticky", Rule(css, ".tabs {"));
    }

    [Fact]
    public void Script_KeepsTheRowsParentOnScreen()
    {
        string script = OnDisk(ScriptFile);
        string css = OnDisk(CssFile);

        // The network is written nested — one node per element with the inputs under it — so the row
        // that owns what is on screen can be read from the rows themselves: the row the editor's top
        // belongs to is the node's own first child, and the parent is the previous sibling of the
        // children container it sits in. Written flat, no row could say whose child it was.
        Assert.Contains("function renderNode(", script);
        Assert.Contains("kids.appendChild(renderNode(element.inputs[i], path.concat(i)))", script);
        Assert.Contains("node.parentElement.previousElementSibling", script);

        // The parent is the row the head is cutting through, when it has children of its own under
        // it — from the moment its row meets the head, so what it is and what it reads survive the
        // head taking it — and otherwise the row that owns the first one the head has not taken. A
        // row with nothing under it is never shown: it parents nothing that is on screen.
        Assert.Contains("if (rect.bottom > line + 1) { first = row; cutByHead = rect.top < line; break; }", script);
        Assert.Contains("const ownChildren = node.children.length > 1;", script);
        Assert.Contains("if (cutByHead && ownChildren) {", script);
        Assert.Contains("parentRow = first;", script);
        Assert.Contains("node.parentElement.previousElementSibling", script);

        // The parent is kept as a readonly copy of its own live line — what it is and what it reads,
        // never a second set of the row's controls — and the poll keeps it painted with the rows,
        // because the copy carries the row's id and its live hook.
        Assert.Contains("function renderPin(", script);
        Assert.Contains("class: 'pin-wrap'", script);
        Assert.Contains("function pinCopy(", script);
        Assert.Contains("class: 'el pin-strip d' + level, 'data-id': row.dataset.id, 'data-live': '1'", script);
        Assert.Contains("live.cloneNode(true)", script);
        // Nothing at the top of the network means no parent, and the strip is put away again.
        Assert.Contains("strip.hidden = !parentRow;", script);

        // The line it rides is the head's own height, measured rather than assumed, and the head is
        // what the script reads it from — on either tab (see the Result test for the other head).
        Assert.Contains("function syncNetPinning(", script);
        Assert.Contains("editor.querySelector('.ed-head, .res-head')", script);
        Assert.Contains("editor.style.setProperty('--net-stick-top'", script);
        Assert.Contains("head.offsetHeight - padTop", script);
        // It follows the scroll one frame at a time, and the editor's scroll is where it listens.
        Assert.Contains("requestAnimationFrame(() => { pinFrame = 0; syncNetPinning(); })", script);
        Assert.Contains("el('editor').addEventListener('scroll', schedulePinnedRows", script);

        // The strip takes no room in the flow: it rides over the rows, so a parent appearing or
        // leaving as the network is scrolled never moves what is under it.
        string wrap = Rule(css, ".pin-wrap {");
        Assert.Contains("position: sticky;", wrap);
        Assert.Contains("top: var(--net-stick-top, 0px);", wrap);
        Assert.Contains("height: 0;", wrap);
        Assert.Contains("position: absolute;", Rule(css, ".pin-strip {"));
    }

    [Fact]
    public void Script_KeepsTheResultsParentOnScreen()
    {
        string script = OnDisk(ScriptFile);
        string css = OnDisk(CssFile);

        // The evaluation arrives as one flat list with a depth per element, and the flat list cannot
        // say whose child a row is. It is folded back into the nesting the editor's own network is
        // written in — one node per element, the deeper ones under it — and written the same way.
        Assert.Contains("const depth = Math.max(0, Math.min(element.depth || 0, open.length));", script);
        Assert.Contains("(depth ? open[depth - 1].children : roots).push(node);", script);
        Assert.Contains("function resultNode(", script);
        Assert.Contains("function resultNet(", script);
        Assert.Contains("class: 'rl-node'", script);
        Assert.Contains("class: 'rl-kids'", script);
        // Both tabs' rows are read by the one strip, so the parent is kept on the Result tab too.
        Assert.Contains("editor.querySelectorAll('.el-node > .el, .rl-node > .rl')", script);
        Assert.Contains("node.parentElement.classList.contains('rl-kids')", script);
        // The card the strip shows is its head, and it carries the definition so the poll repaints
        // the copy exactly as it repaints the row it came from.
        Assert.Contains("if (row.classList.contains('rl'))", script);
        Assert.Contains("head.cloneNode(true)", script);
        Assert.Contains("copy.__def = card.__def;", script);
        Assert.Contains("class: 'rl pin-strip', 'data-id': row.dataset.id, 'data-live': '1'", script);
        // The strip is written into the Result tab's column as well, and the poll's own text — the
        // reason, the progress — is what its head's height is re-measured from.
        Assert.Contains("view.appendChild(renderPin());", script);
        string refresh = Between(script, "function refreshResultHeader(", "\n}\n");
        Assert.Contains("syncNetPinning();", refresh);

        // The verdict itself rides the scroll, carrying the editor's ground and its own rule so
        // nothing shows through the space above the rows while it holds the top.
        string head = Rule(css, ".editor > .res-head {");
        Assert.Contains("position: sticky;", head);
        Assert.Contains("top: calc(-1 * var(--pad-top));", head);
        Assert.Contains("background: var(--bg);", head);
        Assert.Contains("border-bottom: 1px solid var(--border);", head);
        // The evaluated rows nest with the flat list's own spacing.
        string kids = Rule(css, ".rl-node, .rl-kids {");
        Assert.Contains("display: flex;", kids);
        Assert.Contains("gap: 6px;", kids);
    }

    [Fact]
    public void Script_OpensTheResultInANarrowColumnWithAWideControl()
    {
        string script = OnDisk(ScriptFile);
        string css = OnDisk(CssFile);

        // The board is half the editor wide by default — with a readable floor — and the Wide
        // option opens it to the full width. The pinned parent and the rows share that column.
        string view = Rule(css, ".res-view {");
        Assert.Contains("width: max(50%, 320px);", view);
        Assert.Contains("max-width: 100%;", view);
        Assert.Contains("width: 100%;", Rule(css, ".res-view.wide {"));
        Assert.Contains("editor.appendChild(split);", script);
        Assert.Contains("split.appendChild(view);", script);
        Assert.Contains("class: 'res-view' + (resultWide ? ' wide' : '')", script);

        // The verdict header is untouched: it stays a direct child of the editor at full width,
        // and the width control rides the board's first heading instead.
        Assert.Contains("editor.appendChild(h('div', { class: 'res-head' },", script);
        string header = Between(script, "editor.appendChild(h('div', { class: 'res-head' },", "const view = h('div'");
        Assert.DoesNotContain("renderWidthSwitch()", header);
        Assert.Contains("function resultHeading(", script);
        Assert.Contains("h('span', { class: 'res-heading-text', text: text })", script);
        Assert.Contains("renderWidthSwitch());", script);
        Assert.Contains("view.appendChild(resultHeading(", script);
        Assert.Contains("justify-content: space-between;", Rule(css, ".res-heading {"));

        // Narrow is the default: only a stored "wide" opens it, and the heading's two options say
        // which width is showing.
        Assert.Contains("const RESULT_WIDTH_KEY = 'opcbridge.resultWidth';", script);
        Assert.Contains("return localStorage.getItem(RESULT_WIDTH_KEY) === 'wide';", script);
        Assert.Contains("let resultWide = storedResultWide();", script);
        Assert.Contains("renderWidthSwitch()", script);
        Assert.Contains("role: 'group', 'aria-label': 'Result width'", script);
        Assert.Contains("option('Narrow', false)", script);
        Assert.Contains("option('Wide', true)", script);
        Assert.Contains("onclick: () => setResultWidth(wide)", script);
        // The choice is remembered per browser, like the theme.
        Assert.Contains("localStorage.setItem(RESULT_WIDTH_KEY, resultWide ? 'wide' : 'narrow')", script);
    }

    [Fact]
    public void Script_OpensATagInspectorForItsSourceAndReaders()
    {
        string script = OnDisk(ScriptFile);
        string css = OnDisk(CssFile);

        // A result row's tag is a button. Its identity is the area's mapped (source, item) pair when
        // bound, else the name inside its area; an internal relay has no source tag and stays plain.
        Assert.Contains("function tagReference(", script);
        Assert.Contains("const pairTagKey = (sourceId, itemId) =>", script);
        Assert.Contains("const nameTagKey = (area, name) =>", script);
        Assert.Contains("if ((def.relay || '').trim()) return null;", script);
        // A coil has no tag of its own; its tag is the block's OUTPUT tag, so the coil row itself
        // opens the driver's tag. The coil is not counted as a reader of its own output.
        Assert.Contains("def.kind === 'coil' ? (block.outputTag || '').trim() : ''", script);
        Assert.Contains("if (element.kind !== 'coil') add(block, element, element.text, 'read');", script);
        Assert.Contains("function tagUsageIndex(", script);
        Assert.Contains("function tagUsage(", script);
        Assert.Contains("const tagDrivers = (ref) =>", script);
        Assert.Contains("const tagReaders = (ref) =>", script);
        Assert.Contains("function invalidateTagUsage(", script);
        Assert.Contains("roles: []", script);
        // A block's output/coil drives the tag; that is the source the inspector names.
        Assert.Contains("'output ' + (block.outputTag || 'coil'), 'output'", script);
        Assert.Contains("function renderTagLine(", script);
        Assert.Contains("const count = tagReaders(ref).length;", script);
        Assert.Contains("class: 'tag-link' + (tagFocus && tagFocus.ref.key === ref.key ? ' on' : '')", script);
        Assert.Contains("'data-tag-key': ref.key", script);
        Assert.Contains("onclick: () => openTagPanel(ref)", script);
        Assert.Contains("text: uses", script);

        // The inspector is the board's sibling, hidden until a tag is chosen. Source names the
        // block that drives it and the mapped source, Used by lists the blocks reading it.
        Assert.Contains("class: 'res-split'", script);
        Assert.Contains("id: 'resSide'", script);
        Assert.Contains("function renderTagPanel(", script);
        Assert.Contains("function tagPanelSource(", script);
        Assert.Contains("function tagPanelUsage(", script);
        Assert.Contains("function tagUseCard(", script);
        Assert.Contains("tagPanelOption('Source', 'source')", script);
        Assert.Contains("tagPanelOption('Used by (' + uses.length + ')', 'usage')", script);
        Assert.Contains("'aria-label': 'Tag details'", script);
        Assert.Contains("text: 'Driven by'", script);
        Assert.Contains("text: 'Mapped source'", script);
        Assert.Contains("onclick: closeTagPanel", script);
        // A block card lands on that block's Result tab and keeps the inspector beside it.
        Assert.Contains("onclick: () => selectBlock(use.block.id, 'result')", script);
        Assert.Contains("tab = target === 'result' ? 'result' : 'editor';", script);
        Assert.Contains("if (target !== 'result') tagFocus = null;", script);
        // The bridge snapshot feeds its live value, refreshed with the Tags tab's slower cadence.
        Assert.Contains("'data-live-tag': ref.sourceId + '|' + ref.itemId", script);
        Assert.Contains("tab === 'result' && tagFocus", script);

        // The inspector takes the space beside the narrow board and wraps under it if need be.
        string split = Rule(css, ".res-split {");
        Assert.Contains("display: flex;", split);
        Assert.Contains("flex-wrap: wrap;", split);
        string side = Rule(css, ".res-side {");
        Assert.Contains("flex: 1 1 320px;", side);
        Assert.Contains("position: sticky;", side);
        Assert.Contains("display: none;", Rule(css, ".res-side[hidden] {"));
        Assert.Contains("text-decoration: underline dotted;", Rule(css, ".tag-link-label {"));
    }

    [Fact]
    public void Script_DrawsTheNestingRailsTheHmiDraws()
    {
        string script = OnDisk(ScriptFile);
        string css = OnDisk(CssFile);

        // The HMI's own rail: an element row carries one guide per level enclosing it, beside the row
        // card, so a deep network reads as a tree (LogicElementRowView, and the same on the result
        // rows). Here the children's container draws them — a box spans its whole group, so the
        // lines hold across the gaps between the cards, where a guide per row would break — and the
        // container carries the depth of the rows inside it.
        Assert.Contains("class: 'el-kids', style: '--depth:' + (depth + 1)", script);
        Assert.Contains("class: 'rl-kids', style: '--depth:' + (depth + 1)", script);
        Assert.Contains("position: relative;", Rule(css, ".el-kids, .rl-kids {"));
        string rail = Rule(css, ".el-kids::before, .rl-kids::before {");
        Assert.Contains("position: absolute;", rail);
        Assert.Contains("top: 0;", rail);
        Assert.Contains("bottom: 0;", rail);
        Assert.Contains("width: calc(var(--depth, 0) * 18px);", rail);
        Assert.Contains("background: repeating-linear-gradient(to right, var(--border2) 0 1px, transparent 1px 18px);", rail);

        // A guide every 18px is only beside the row that owns its level because a row indents on that
        // same step: the innermost line lands on the card edge of the row above.
        Assert.Contains("margin-left: calc(var(--depth, 0) * 18px);", Rule(css, ".el {"));
        Assert.Contains("margin-left: calc(var(--depth, 0) * 18px);", Rule(css, ".rl {"));

        // The evaluated rows are drawn at the depth the flat list was folded to, not the number the
        // bridge sent, because the lines are drawn for the folded depth (resultTree); and the
        // read-only expansion of a flat-conditions block nests the same way, so the Editor tab draws
        // its rows behind the same guides whether the network is editable or not.
        Assert.Contains("function resultNode(node, index, depth)", script);
        Assert.Contains("resultRow(node.element, index.byId.get(node.element.id), depth)", script);
        Assert.Contains("net.appendChild(resultNode(node, index, 0))", script);
        Assert.Contains("style: '--depth:' + depth", script);
        Assert.Contains("function readOnlyNode(", script);
        // The read-only rows carry their definitions too, so a flat-conditions block's contacts
        // show the same external-device reading as the editable network.
        Assert.Contains("const defsById = indexBlock(draft).byId;", script);
        Assert.Contains("for (const node of resultTree(list)) net.appendChild(readOnlyNode(node, 0, defsById));", script);
        Assert.Contains("class: 'el readonly d' + Math.min(depth, 3)", script);
    }

    [Fact]
    public void Script_KeepsTheEditorsScrollWhenItRebuilds()
    {
        string script = OnDisk(ScriptFile);

        // The editor is the page's own scroll region, and every row edit rewrites it from the top.
        // The offset is read before the rebuild and put back after it, so adding a row while
        // scrolled down keeps the author where they were instead of throwing the view to the top.
        string render = Between(script, "function renderEditor(", "\n}\n");
        Assert.Contains("const scrollTop = resetScroll ? 0 : editor.scrollTop;", render);
        Assert.Contains("drawEditor(editor);", render);
        Assert.Contains("if (editor.scrollTop !== scrollTop) editor.scrollTop = scrollTop;", render);
        // The pinned parent is measured from the restored scroll, not from the top it started at.
        Assert.Contains("syncNetPinning();", render);

        // Adding a row at any level is a rebuild of this document and keeps the offset. The new
        // row itself is brought into view by the least scroll that clears the head pinned above
        // it and the save bar stuck below it — it never throws the author to the top instead.
        Assert.Contains("function addElementRow(list, at) {", script);
        Assert.Contains("onclick: () => { addElementRow(siblings, index + 1); }", script);
        Assert.Contains("onclick: () => { addElementRow(element.inputs, element.inputs.length); }", script);
        Assert.Contains("onclick: () => { addElementRow(draft.elements, draft.elements.length); }", script);
        Assert.Contains("revealElementId = fresh.id;", script);
        Assert.Contains("if (revealElementId) {", render);
        Assert.Contains("revealRow(editor, revealElementId);", render);
        Assert.Contains("function revealRow(editor, id) {", script);
        Assert.Contains("const bottom = Math.min(barTop, view.bottom);", script);
        Assert.Contains("editor.scrollTop += box.bottom - bottom + 6;", script);

        // A block or tab switch is a move to another document and starts at the top.
        Assert.Contains("onclick: () => { tab = id; renderEditor(true); }", script);
        Assert.Contains("renderEditor(true);\n  renderResult();", script);
    }

    [Fact]
    public void Script_ShadesTheEditorsRowsByNestingDepth()
    {
        string script = OnDisk(ScriptFile);
        string css = OnDisk(CssFile);

        // The editor's stripe says how deep a row sits in the network — the root darkest, every
        // level under it one shade lighter, clamped at the third level. It never says which kind
        // the row is: the gate / function-block colours are gone, so a timer and an AND no longer
        // read as different things.
        Assert.Contains("const cls = 'el d' + Math.min(depth, 3);", script);
        Assert.Contains("class: 'el readonly d' + Math.min(depth, 3)", script);
        Assert.Contains("class: 'el pin-strip d' + level", script);
        Assert.Contains(".el.d0 { border-left-color: var(--depth-0); }", css);
        Assert.Contains(".el.d1 { border-left-color: var(--depth-1); }", css);
        Assert.Contains(".el.d2 { border-left-color: var(--depth-2); }", css);
        Assert.Contains(".el.d3 { border-left-color: var(--depth-3); }", css);
        Assert.DoesNotContain(".el.gate", css);
        Assert.DoesNotContain(".el.fb", css);
        Assert.DoesNotContain(".el.parent", css);
    }

    [Fact]
    public void Script_ShowsTheOutputTagAndItsSecondCheck()
    {
        string script = OnDisk(ScriptFile);
        string css = OnDisk(CssFile);

        // The output tag is one per block, read as feedback only; a new name joins the area's
        // list unassigned, like a row's + New tag.
        Assert.Contains("outputTag: ''", script);
        Assert.Contains("title: 'This block\\'s output/coil tag — read as feedback only. Its source and tag are assigned on the Tags tab.'", script);
        Assert.Contains("if (name) rememberTagName(name);", script);

        // The rail names the tag beside the block and carries a second dot for the output, so
        // the interlock check and the feedback check read apart.
        Assert.Contains("class: 'block-output mono', text: outputTag", script);
        Assert.Contains("class: 'out-dot ' + (output ? output.state : 'none')", script);
        Assert.Contains("if (output.state === 'mismatch') return 'MISMATCH';", script);
        Assert.Contains("if (output.state === 'unknown') return 'UNVERIFIED';", script);
        Assert.Contains("return 'OUTPUT ' + tag + ' · ' + outputWord(output) + outputActual(output);", script);

        // The Result header's own pill, and the blink that marks a disagreement.
        Assert.Contains("id: 'resOutput'", script);
        Assert.Contains("outputPill.className = 'pill out-pill ' + (output ? (output.state || 'none') : 'unknown');", script);
        Assert.Contains("@keyframes output-blink", css);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css);

        // The page authors no relays: a relay from an HMI-authored block is shown read-only.
        Assert.DoesNotContain("class: 'w-relay", script);
        Assert.DoesNotContain("'Relay the coil drives'", script);
        Assert.DoesNotContain("text: '(internal relay)'", script);
        Assert.Contains("class: 'legacy-relay mono small'", script);

        // The verified colour is a token in both themes; the theme test also holds the sets equal.
        Assert.Contains("--verify:", css);
    }

    [Fact]
    public void Script_TakesTheEvaluationFromTheHub()
    {
        string html = OnDisk(HtmlFile);
        string script = OnDisk(ScriptFile);

        // The vendored client loads before the page's own script (both deferred, so in order), or
        // `signalR` would not exist when start() runs.
        int client = html.IndexOf("src=\"/logic/signalr.min.js\"", StringComparison.Ordinal);
        int own = html.IndexOf("src=\"/logic/app.js\"", StringComparison.Ordinal);
        Assert.True(client >= 0, "the page does not load the vendored SignalR client");
        Assert.True(own > client, "app.js must load after the SignalR client");

        // The bridge's own hub and event, subscribed with the HMI's retry schedule: at once, then
        // 1/2/5 s, then every 5 s (HmiHubTiming).
        Assert.Contains("const HUB_PATH = '/hmi';", script);
        Assert.Contains("new signalR.HubConnectionBuilder()", script);
        Assert.Contains(".withUrl(HUB_PATH)", script);
        Assert.Contains(".withAutomaticReconnect({", script);
        Assert.Contains("nextRetryDelayInMilliseconds:", script);
        // The first retry is 0 ms — a falsy-zero fallback would put it on the 5 s tail instead.
        Assert.Contains("retry.previousRetryCount < HUB_RETRY_MS.length", script);
        // An initial start failure retries too; the fallback poll carries the page until it takes.
        Assert.Contains("setTimeout(startHub, 5000)", script);
        Assert.Contains("hub.on('logic', (snapshot) => {", script);

        // A push lands through the same apply path as a poll and never rebuilds the page: the
        // rows' node identity, the focus and anything typed must survive it.
        string handler = Between(script, "hub.on('logic'", "hub.onreconnecting(");
        Assert.Contains("noteState();", handler);
        Assert.Contains("applyState(snapshot);", handler);
        Assert.DoesNotContain("renderEditor(", handler);
        Assert.DoesNotContain("drawEditor(", handler);

        // The 1 Hz poll is the fallback, not the feed: the push moves the loop to the slow
        // refresh, and a lost hub puts it back on the second.
        Assert.Contains("const POLL_MS = 1000;", script);
        Assert.Contains("const LIVE_REFRESH_MS = 5000;", script);
        Assert.Contains("const period = hubLive ? LIVE_REFRESH_MS : POLL_MS;", script);
        Assert.Contains("hub.onreconnecting(() => { hubLive = false; schedulePoll(); });", script);
        Assert.Contains("hub.onclose(() => { hubLive = false; schedulePoll(); });", script);
        // A reconnect reads the state at once: the bridge does not re-push a signature it already
        // sent, so a snapshot missed while the link was down would otherwise never arrive.
        Assert.Contains("hub.onreconnected(() => { hubLive = true; schedulePoll(); poll(); });", script);
        Assert.Contains("hub.start()", script);
        Assert.Contains("startHub();", script);
        // The fallback still reads the same endpoint the push carries.
        Assert.Contains("state: '/api/logic/state'", script);
    }

    // The vendored SignalR client is deliberately not in this theory: its code is a build-time
    // copy of the official browser client, and its diagnostics mention Microsoft documentation
    // URLs. What matters is that the page *loads* nothing from the network, which the three files
    // it authors are held to here.
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
