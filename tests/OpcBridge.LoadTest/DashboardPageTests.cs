using OpcBridge.App;
using Xunit;

namespace OpcBridge.LoadTest;

public sealed class DashboardPageTests
{
    [Fact]
    public void Html_UsesDedicatedDaLinksBrowseWorkflow()
    {
        Assert.DoesNotContain("id=\"fpProvider\"", DashboardPage.Html);
        Assert.DoesNotContain("Set up links from a tag's faceplate", DashboardPage.Html);
        Assert.DoesNotContain("id=\"linkConsumerSelect\"", DashboardPage.Html);
        Assert.DoesNotContain("id=\"linkProviderSelect\"", DashboardPage.Html);
        Assert.Contains("DA Links", DashboardPage.Html);
        Assert.Contains("id=\"linkSourceStatus\"", DashboardPage.Html);
        Assert.Contains("id=\"linkBrowseTree\"", DashboardPage.Html);
    }

    [Fact]
    public void Script_BrowsesDaTagsForLinksInsteadOfReusingMappings()
    {
        Assert.Contains("/api/da-links", DashboardPage.Script);
        Assert.Contains("function browseLinkTags(", DashboardPage.Script);
        Assert.Contains("state.linkDraft", DashboardPage.Script);
        Assert.Contains("data-action=\"pick-link-consumer\"", DashboardPage.Script);
        Assert.Contains("data-action=\"pick-link-provider\"", DashboardPage.Script);
        Assert.DoesNotContain("el('linkConsumerSelect').innerHTML = opts;", DashboardPage.Script);
        Assert.DoesNotContain("el('linkProviderSelect').innerHTML = opts;", DashboardPage.Script);
        Assert.DoesNotContain("const opts = '<option value=\"\">— select —</option>' + mappings.map", DashboardPage.Script);
    }

    [Fact]
    public void LinkDraft_CanBeClearedWithoutDeletingSavedRule()
    {
        Assert.Contains("id=\"btnClearLinkSelection\"", DashboardPage.Html);
        Assert.Contains(">Clear Selection<", DashboardPage.Html);
        Assert.Contains(">Delete Saved Link<", DashboardPage.Html);
        Assert.Contains("function clearLinkDraftSelection()", DashboardPage.Script);
        Assert.Contains("state.linkDraft.consumer = null", DashboardPage.Script);
        Assert.Contains("state.linkDraft.provider = null", DashboardPage.Script);
    }

    [Fact]
    public void Html_ContainsDriversRouteAndA3nControls()
    {
        Assert.Contains("connectivity/drivers", DashboardPage.Html);
        Assert.Contains("drvA3nPort", DashboardPage.Html);
        Assert.Contains("sourceType", DashboardPage.Script); // JS save payload
    }

    [Fact]
    public void Html_ContainsDriversNavViewFormAndWizard()
    {
        Assert.Contains("data-tab=\"drivers\"", DashboardPage.Html);
        Assert.Contains("id=\"view-drivers\"", DashboardPage.Html);
        Assert.Contains("id=\"wzDrv\"", DashboardPage.Html);
        foreach (string id in new[]
        {
            "drvA3nSourceId", "drvA3nName", "drvA3nPort", "drvA3nBaud", "drvA3nDataBits",
            "drvA3nParity", "drvA3nStopBits", "drvA3nStation", "drvA3nPc", "drvA3nTimeout",
            "drvA3nRetry", "drvA3nRate", "drvA3nMaxTags"
        })
        {
            Assert.Contains($"id=\"{id}\"", DashboardPage.Html);
        }
    }

    [Fact]
    public void Html_HasDedicatedMxComponentSectionSeparateFromDrivers()
    {
        // MX Component connections live on their own page, not in the serial-drivers section.
        Assert.Contains("data-tab=\"mx-component\"", DashboardPage.Html);
        Assert.Contains("id=\"view-mx-component\"", DashboardPage.Html);
        Assert.Contains("id=\"mxStation\"", DashboardPage.Html);
        Assert.Contains("id=\"mxList\"", DashboardPage.Html);
        Assert.Contains("'connectivity/mx-component': 'mx-component'", DashboardPage.Script);
        Assert.Contains("function renderMx(", DashboardPage.Script);
        Assert.Contains("function saveMxSource(", DashboardPage.Script);
        Assert.Contains("/api/drivers/mx-component/test-connection", DashboardPage.Script);
        // MX is no longer offered in the serial-driver wizard, and isDriverSource is serial-only.
        Assert.DoesNotContain("<option value=\"MxComponent\">", DashboardPage.Html);
        Assert.DoesNotContain("drvMxStation", DashboardPage.Html);
        Assert.Contains("function isDriverSource(source) { return isMelsecSource(source) || isS7Source(source); }", DashboardPage.Script);
        Assert.Contains("if (type === 'mx') return mxSources();", DashboardPage.Script);
        Assert.Contains("data-map-type=\"mx\"", DashboardPage.Html);
    }

    [Fact]
    public void Script_RoutesDriversTabAndSavesMelsecSource()
    {
        Assert.Contains("'connectivity/drivers': 'drivers'", DashboardPage.Script);
        Assert.Contains("function renderDrivers(", DashboardPage.Script);
        Assert.Contains("function saveDriverSource(", DashboardPage.Script);
        Assert.Contains("function testDriverConnection(", DashboardPage.Script);
        Assert.Contains("/api/drivers/melsec-a3n/test-connection", DashboardPage.Script);
        Assert.Contains("/api/drivers/s7200-ppi/test-connection", DashboardPage.Script);
        Assert.Contains("sourceType: type", DashboardPage.Script);
        Assert.Contains("S7200Ppi", DashboardPage.Script);
        Assert.Contains("btnDrvScanPorts", DashboardPage.Html);
        Assert.Contains("btnWzDrvScanPorts", DashboardPage.Html);
        Assert.Contains("function scanSerialPorts(", DashboardPage.Script);
        Assert.Contains("/api/serial/ports", DashboardPage.Script);
    }

    [Fact]
    public void Script_SaveSourceRefusesToOverwriteMelsecSource()
    {
        Assert.Contains("if (existing && isDriverSource(existing))", DashboardPage.Script);
        Assert.Contains("const saved = await saveDriverSource();", DashboardPage.Script);
        Assert.Contains("if (!saved) return;", DashboardPage.Script);
    }

    [Fact]
    public void Html_ContainsAppsPill()
    {
        Assert.Contains("id=\"pApps\"", DashboardPage.Html);
        Assert.Contains("Apps", DashboardPage.Html);
    }

    [Fact]
    public void Script_UpdatesAppsPillFromDetectedCount()
    {
        Assert.Contains("pApps", DashboardPage.Script);
        Assert.Contains("detectedCount", DashboardPage.Script);
    }

    [Fact]
    public void Html_ContainsInfluxTabAndFaceplateToggle()
    {
        Assert.Contains("data-tab=\"influx\"", DashboardPage.Html);
        Assert.Contains("id=\"view-influx\"", DashboardPage.Html);
        Assert.Contains("id=\"fpInfluxEnabled\"", DashboardPage.Html);
        Assert.Contains("id=\"influxUrl\"", DashboardPage.Html);
        Assert.Contains("id=\"influxWritten\"", DashboardPage.Html);
    }

    [Fact]
    public void Script_LoadsAndSavesInfluxConfig()
    {
        Assert.Contains("function loadInfluxConfig(", DashboardPage.Script);
        Assert.Contains("function loadInfluxStatus(", DashboardPage.Script);
        Assert.Contains("function saveInflux(", DashboardPage.Script);
        Assert.Contains("function connectInflux(", DashboardPage.Script);
        Assert.Contains("function disconnectInflux(", DashboardPage.Script);
        Assert.Contains("/api/influx/config", DashboardPage.Script);
        Assert.Contains("influxEnabled: el('fpInfluxEnabled').checked", DashboardPage.Script);
        Assert.Contains("if (name === 'influx')", DashboardPage.Script);
    }

    [Fact]
    public void Html_SeparatesMqttAndTrafficViews()
    {
        Assert.Contains("data-tab=\"mqtt\"", DashboardPage.Html);
        Assert.Contains("data-tab=\"iot-traffic\"", DashboardPage.Html);
        Assert.Contains("id=\"view-mqtt\"", DashboardPage.Html);
        Assert.Contains("id=\"view-iot-traffic\"", DashboardPage.Html);
        Assert.Contains("id=\"mqttTraffic\"", DashboardPage.Html);
        Assert.DoesNotContain("const activeTab = name === 'iot-traffic' ? 'mqtt' : name;", DashboardPage.Script);
        Assert.Contains("if (activeTab === 'iot-traffic')", DashboardPage.Script);
        Assert.Contains("#view-iot-traffic.active", DashboardPage.Script);
        Assert.Contains("'iot/traffic': 'iot-traffic'", DashboardPage.Script);
    }

    [Fact]
    public void Script_OpensWizardsWithModalOpenClass()
    {
        // .modal-overlay { display:none } + .modal-overlay.open { display:flex }
        // Inline style.display='' loses to the stylesheet and leaves wizards invisible.
        Assert.Contains(".modal-overlay.open", DashboardPage.Html);
        Assert.Contains("el('addSourceWizard').classList.add('open')", DashboardPage.Script);
        Assert.Contains("el('addSourceWizard').classList.remove('open')", DashboardPage.Script);
        Assert.Contains("el('wzDrv').classList.add('open')", DashboardPage.Script);
        Assert.Contains("el('wzDrv').classList.remove('open')", DashboardPage.Script);
        Assert.Contains("el('mqttWizard').classList.add('open')", DashboardPage.Script);
        Assert.Contains("el('mqttWizard').classList.remove('open')", DashboardPage.Script);
        Assert.Contains("el('influxWizard').classList.add('open')", DashboardPage.Script);
        Assert.Contains("el('influxWizard').classList.remove('open')", DashboardPage.Script);
        Assert.DoesNotContain("el('addSourceWizard').style.display", DashboardPage.Script);
        Assert.DoesNotContain("el('mqttWizard').style.display", DashboardPage.Script);
        Assert.DoesNotContain("el('influxWizard').style.display", DashboardPage.Script);
        Assert.DoesNotContain("el('wzDrv').style.display = ''", DashboardPage.Script);
        Assert.DoesNotContain("el('wzDrv').style.display = 'none'", DashboardPage.Script);
        Assert.DoesNotContain("id=\"addSourceWizard\" style=\"display:none\"", DashboardPage.Html);
        Assert.DoesNotContain("id=\"wzDrv\" style=\"display:none\"", DashboardPage.Html);
        Assert.DoesNotContain("id=\"mqttWizard\" style=\"display:none\"", DashboardPage.Html);
        Assert.DoesNotContain("id=\"influxWizard\" style=\"display:none\"", DashboardPage.Html);
    }
    [Fact]
    public void Script_BrowsesUaSourceViaUaBrowseApi()
    {
        Assert.Contains("async function browseUaSource(", DashboardPage.Script);
        Assert.Contains("/api/ua/browse", DashboardPage.Script);
        Assert.Contains("isUaSource(source)", DashboardPage.Script);
        Assert.Contains("if (isUaSource(currentSource()))", DashboardPage.Script);
        Assert.Contains("nodeId: targetNodeId", DashboardPage.Script);
        Assert.Contains("maxNodes: 200", DashboardPage.Script);
    }

    [Fact]
    public void Script_UaVariableMapActionPostsNodeIdAsDaItemId()
    {
        Assert.Contains("data-action=\"add-tag\"", DashboardPage.Script);
        Assert.Contains(">Map<", DashboardPage.Script);
    }

    [Fact]
    public void Script_DaSourceStillUsesDaTagsApi()
    {
        Assert.Contains("/api/da/tags", DashboardPage.Script);
        Assert.Contains("function browseTags(", DashboardPage.Script);
    }

    [Fact]
    public void Script_LinkBrowseRefusesNonDaSource()
    {
        // DA Links forward DA→DA only. The active source can be a UA/driver/MX source
        // selected on another tab, so the link browser must refuse to post it to the
        // OPC DA browse endpoint instead of failing with a confusing error.
        Assert.Contains("if (!sourceMatchesMapType(source, 'opc-da')) {", DashboardPage.Script);
        Assert.Contains("DA Links browse OPC DA sources only", DashboardPage.Script);
        Assert.Contains("DA Links require an OPC DA source", DashboardPage.Script);
    }

    [Fact]
    public void Script_BrowseRefusesSourceOutsideActiveMapType()
    {
        // The maps tag browser must never fall through to the OPC DA browse when a
        // source of a different type is selected (e.g. a DA source left selected while
        // the OPC UA map tab has no UA sources). Browse must refuse instead.
        Assert.Contains("if (!sourceMatchesMapType(source)) {", DashboardPage.Script);
        Assert.Contains("This tab browses ${mapTypeLabel()} sources only", DashboardPage.Script);
    }

    [Fact]
    public void Html_MapsHasSourceTypeSubTabs()
    {
        Assert.Contains("id=\"mapTypeTabs\"", DashboardPage.Html);
        Assert.Contains("data-map-type=\"opc-da\"", DashboardPage.Html);
        Assert.Contains("data-map-type=\"opc-ua\"", DashboardPage.Html);
        Assert.Contains("data-map-type=\"drivers\"", DashboardPage.Html);
        Assert.Contains(">Source<", DashboardPage.Html);
        Assert.DoesNotContain(">DA Source<", DashboardPage.Html);
    }

    [Fact]
    public void Script_MapsFiltersBySourceType()
    {
        Assert.Contains("function setMapType(", DashboardPage.Script);
        Assert.Contains("function opcDaSources(", DashboardPage.Script);
        Assert.Contains("function mapTypeSources(", DashboardPage.Script);
        Assert.Contains("function mappingsForMapType(", DashboardPage.Script);
        Assert.Contains("tags/maps/opc-da", DashboardPage.Script);
        Assert.Contains("tags/maps/opc-ua", DashboardPage.Script);
        Assert.Contains("tags/maps/drivers", DashboardPage.Script);
        Assert.Contains("mapType: 'opc-da'", DashboardPage.Script);
    }

    [Fact]
    public void Script_MappingRowsShowDataTypePill()
    {
        // Maps tab rows carry a type pill: runtime type from the live value when
        // available (same source as Live Values), configured type otherwise.
        Assert.Contains("const live = currentValue(sourceId, item);", DashboardPage.Script);
        Assert.Contains("const mappedType = (live && get(live, 'dataType')) || mapping.dataType || mapping.DataType || '—';", DashboardPage.Script);
        Assert.Contains("typeBadge", DashboardPage.Script);
    }

    [Fact]
    public void Html_MappingRowBadgesStayOnOneLine()
    {
        // Badge cluster must never wrap vertically — single line with clipped
        // overflow (fade) and a tooltip summarizing the full status set.
        Assert.Contains(".li .li-badge { margin-left: auto; display: flex;", DashboardPage.Html);
        Assert.Contains("flex-wrap: nowrap; overflow: hidden;", DashboardPage.Html);
        Assert.Contains("mask-image: linear-gradient(to right", DashboardPage.Html);
        Assert.Contains("const statusSummary", DashboardPage.Script);
        Assert.Contains("title=\"${attr(statusSummary)}\"", DashboardPage.Script);
    }

    [Fact]
    public void Html_MappingStatusBadgePinnedRightAndNeverClipped()
    {
        // The colored access status belongs at the far right of the row: config
        // badges (type/deadband/rate/MQTT/Influx) form a clipping group while the
        // status (connection-state + access) sits outside it with flex-shrink:0 so
        // it can never be cut off.
        Assert.Contains(".li .li-badge-clip { display: flex;", DashboardPage.Html);
        Assert.Contains(".li .li-badge-status { flex-shrink: 0; margin-left: 2px;", DashboardPage.Html);
        Assert.Contains("<span class=\"li-badge-clip\">${typeBadge}${deadbandBadge}${rateBadge}${mqttBadge}${influxBadge}</span><span class=\"li-badge-status\">${discBadge ? `<span title=\"${attr(discTitle)}\">${discBadge}</span>` : ''}${accessBadge}</span>", DashboardPage.Script);
    }

    [Fact]
    public void Script_MappingRowsShowDisconnectedBadge()
    {
        // Disc/Bad badges are driven by server-side signals, never by absence from the
        // capped value window: failed monitored items (auto-retrying), bad-quality values,
        // and the per-source connection state. The refresh() payload populates the sets.
        Assert.Contains("state.disconnectedKeys = new Set((p.disconnected || []).map(d => valueKey(get(d, 'sourceId') || '', get(d, 'itemId') || '')));", DashboardPage.Script);
        Assert.Contains("state.badQualityKeys = new Set((p.badQuality || []).map(d => valueKey(get(d, 'sourceId') || '', get(d, 'itemId') || '')));", DashboardPage.Script);
        Assert.Contains("state.disconnectedSources = new Set((sources || []).filter(s => String(get(s, 'connectionState') || '').toLowerCase() !== 'connected')", DashboardPage.Script);
        Assert.Contains("const sourceDown = enabled && state.disconnectedSources.has(sourceId);", DashboardPage.Script);
        Assert.Contains("const failedItem = enabled && state.disconnectedKeys.has(valueKey(sourceId, item));", DashboardPage.Script);
        Assert.Contains("const badQuality = enabled && state.badQualityKeys.has(valueKey(sourceId, item));", DashboardPage.Script);
        Assert.Contains("discTitle = 'Disconnected — no value received (auto-retrying)';", DashboardPage.Script);
        Assert.Contains("discTitle = 'Bad quality from source';", DashboardPage.Script);
        // The summary tooltip carries the connection state too.
        Assert.Contains("failedItem ? 'Disconnected (auto-retrying)' : null", DashboardPage.Script);
        Assert.Contains("badQuality ? 'Bad quality' : null", DashboardPage.Script);
        // The badge must not depend on the sampled value window.
        Assert.DoesNotContain("enabled && !live", DashboardPage.Script);
        Assert.DoesNotContain("!liveGood", DashboardPage.Script);
        // Refresh re-renders the Maps rows while that tab is visible so badges track live state.
        Assert.Contains("if (document.querySelector('.tabbtn.active')?.dataset.tab === 'tags') {", DashboardPage.Script);
        Assert.Contains("rerenderMappings();", DashboardPage.Script);
    }

    [Fact]
    public void Script_FaceplateLivePanelShowDataType()
    {
        // Faceplate real-value panel shows the tag's data type, with the mapping's
        // configured type as fallback when no live value exists yet.
        Assert.Contains("function renderLiveValue(value, fallbackType)", DashboardPage.Script);
        Assert.Contains("const type = get(value, 'dataType') || fallbackType || '—';", DashboardPage.Script);
        Assert.Contains("renderLiveValue(currentValue(sourceId, itemId), mapping.dataType || mapping.DataType || null)", DashboardPage.Script);
        // The panel header label is redundant — the big value + meta row carry the context.
        Assert.DoesNotContain("Real value", DashboardPage.Script);
    }

    [Fact]
    public void Css_DefinesDaGroupsTableStyles()
    {
        // .tbl was referenced by the DA Groups renderer but never defined, so the
        // table rendered as an unstyled browser table (cramped, no header row).
        Assert.Contains(".tbl th {", DashboardPage.Html);
        Assert.Contains(".tbl td {", DashboardPage.Html);
        Assert.Contains(".tbl tbody tr:hover", DashboardPage.Html);
    }

    [Fact]
    public void DaGroups_V3UsesCardGridWithModalEditor()
    {
        // v3 redesign: groups render as cards in a responsive grid; add/edit happen
        // in a modal dialog. No table, no tfoot, no inline-row editing anywhere in
        // the DA Groups panel — nothing can repaint under the cursor mid-typing.
        Assert.Contains("dag-card", DashboardPage.Html);
        Assert.Contains("dag-grid", DashboardPage.Html);
        Assert.DoesNotContain("<tfoot>", DashboardPage.Script);
        Assert.DoesNotContain("daGroupsTfootHtml", DashboardPage.Script);

        // Modal editor: markup + open/save/close functions.
        Assert.Contains("id=\"dagModal\"", DashboardPage.Html);
        Assert.Contains("id=\"dagModalName\"", DashboardPage.Html);
        Assert.Contains("id=\"dagModalRate\"", DashboardPage.Html);
        Assert.Contains("id=\"dagModalIo\"", DashboardPage.Html);
        Assert.Contains("function openDagAdd(", DashboardPage.Script);
        Assert.Contains("function openDagEdit(", DashboardPage.Script);
        Assert.Contains("function dagModalSave(", DashboardPage.Script);
        Assert.Contains("function closeDagModal(", DashboardPage.Script);
    }

    [Fact]
    public void DaGroups_PreservesElementIdsAndHandlers()
    {
        // The panel container ids and the expand/collapse + delete flows stay.
        foreach (string fragment in new[]
        {
            "daGroupsHint-", "daGroupsTable-", "daGroupsMsg",
            "function deleteDaGroup(",
            "function expandAllDaGroups(", "function collapseAllDaGroups(",
            "function loadDaGroupsTab("
        })
        {
            Assert.Contains(fragment, DashboardPage.Script);
        }
        // v2/v1 machinery is gone for good.
        foreach (string fragment in new[]
        {
            "saveDaGroupEdit", "ensureDaGroupAddControls", "mountDaGroupAddRow",
            "stowDaGroupAddControls", "_dagNodes"
        })
        {
            Assert.DoesNotContain(fragment, DashboardPage.Script);
        }
    }

    [Fact]
    public void DaGroups_ReloadsAreSequencedPerSource()
    {
        // Regression: racing follow-up GETs could repaint older data over newer
        // ("sometimes added, sometimes not"). Whichever reload started latest wins.
        Assert.Contains("state.daGroupRenderSeq", DashboardPage.Script);
        Assert.Contains("state.daGroupRenderSeq[sourceId] = seq", DashboardPage.Script);
    }

    [Fact]
    public void DaGroups_AddButtonDisablesWhileInFlight()
    {
        Assert.Contains(".disabled = true", DashboardPage.Script);
        Assert.Contains(".disabled = false", DashboardPage.Script);
    }

    [Fact]
    public void OpcDaView_GroupsSectionIsReadOnlySummary()
    {
        // OPC DA view shows groups read-only (total + name·rate·io badges) with
        // a Manage button jumping to the DA Groups panel — editing lives there.
        Assert.Contains(">Manage groups<", DashboardPage.Script);
        Assert.DoesNotContain("function applyGroupMode(", DashboardPage.Script);
        Assert.DoesNotContain("function resetGroupMode(", DashboardPage.Script);
    }

    [Fact]
    public void Faceplate_UpdateRateSelectsDaGroup()
    {
        // UPDATE RATE on the faceplate Setup tab selects a named DA group:
        // options come from the per-source group cache, saving stores daGroup
        // (and aligned pollRateMs), legacy numeric rates stay selectable.
        Assert.Contains("function ensureDaGroupsCache(", DashboardPage.Script);
        Assert.Contains("function fpRateOptions(", DashboardPage.Script);
        Assert.Contains("payload.daGroup", DashboardPage.Script);
    }

    [Fact]
    public void GroupRenamePropagatesToMappings()
    {
        // Renaming a group in the DA Groups modal tells the server the old name
        // so mapping references (TagMapping.DaGroup) are rewritten, not orphaned.
        Assert.Contains("renameFrom", DashboardPage.Script);
    }
}
