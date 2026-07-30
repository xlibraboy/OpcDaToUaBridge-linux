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
}
