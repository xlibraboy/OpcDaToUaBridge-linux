using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using OpcBridge.App;
using OpcBridge.App.Hmi;
using OpcBridge.Client;
using OpcBridge.Core;
using Xunit;

namespace OpcBridge.LoadTest;

/// <summary>
/// The broadcaster pushes a <c>logic</c> snapshot on the first evaluation and then only
/// when the derived states actually change — an ordinary value move inside the same state
/// must not re-push it.
/// </summary>
[Collection(nameof(InterlinkApiAppCollection))]
public sealed class LogicBroadcastTests : IDisposable
{
    // The store persists to a fixed file under AppContext.BaseDirectory, which is also the
    // directory the app-backed tests copy their instance from: clear it at the end of each
    // test (xUnit disposes each test instance) so no block leaks into another test app.
    public void Dispose() => DeleteLogicFile();
    [Fact]
    public void PushLogicIfChanged_SendsOnceAndThenOnlyOnStateChange()
    {
        HmiBroadcastService service = CreateService(out BridgeState values, out _, out FakeHubContext hub);

        values.SetValue(new BridgeValue("sim", "Permit", true, DateTime.UtcNow, 192, true));
        service.PushLogicIfChanged();

        Assert.Single(hub.Sent);
        (string firstMethod, object? firstPayload) = hub.Sent[0];
        Assert.Equal("logic", firstMethod);
        LogicStateSnapshot first = Assert.IsType<LogicStateSnapshot>(firstPayload);
        Assert.Equal(LogicBlockStates.Ready, first.Blocks[0].State);
        Assert.Equal(LogicConditionStates.True, first.Blocks[0].Elements.Single(element => element.Kind == LogicElementKinds.Contact).State);
        // No mapping is seeded in this test, so a Boolean value renders as the plain On/Off
        // text; the mapping-driven texts (OnText/OffText, decimals, unit) are covered in
        // LogicStateEvaluatorTests.ValueText_UsesDigitalTextsAnalogDecimalsAndUnit.
        Assert.Equal("On", first.Blocks[0].Elements.Single(element => element.Kind == LogicElementKinds.Contact).ValueText);

        // Same state, new sample after the same value: no push.
        values.SetValue(new BridgeValue("sim", "Permit", true, DateTime.UtcNow, 192, true));
        service.PushLogicIfChanged();
        Assert.Single(hub.Sent);

        // The condition flips: one push carrying the blocked state with the reason.
        values.SetValue(new BridgeValue("sim", "Permit", false, DateTime.UtcNow, 192, true));
        service.PushLogicIfChanged();

        Assert.Equal(2, hub.Sent.Count);
        LogicStateSnapshot second = Assert.IsType<LogicStateSnapshot>(hub.Sent[1].Payload);
        Assert.Equal(LogicBlockStates.Blocked, second.Blocks[0].State);
        Assert.Equal("Line 01 start permit must be given", second.Blocks[0].Reason);

        // Bad quality is a state change too (blocked false -> unknown).
        values.SetValue(new BridgeValue("sim", "Permit", false, DateTime.UtcNow, 0, false));
        service.PushLogicIfChanged();

        Assert.Equal(3, hub.Sent.Count);
        LogicStateSnapshot third = Assert.IsType<LogicStateSnapshot>(hub.Sent[2].Payload);
        Assert.Equal(LogicBlockStates.Unknown, third.Blocks[0].State);
    }

    [Fact]
    public void PushLogicIfChanged_DoesNotDuplicateWhenOnlyTheDefinitionTextChanges()
    {
        HmiBroadcastService service = CreateService(out BridgeState values, out LogicStore logic, out FakeHubContext hub);

        values.SetValue(new BridgeValue("sim", "Permit", true, DateTime.UtcNow, 192, true));
        service.PushLogicIfChanged();
        Assert.Single(hub.Sent);

        LogicBlockDto renamed = logic.GetSnapshot().Blocks.Single();
        renamed.Name = "Line 01 Start (renamed)";
        Assert.True(logic.TrySave(renamed, out _, out _, out _));

        // The signature covers derived states only, so a text-only edit pushes nothing.
        service.PushLogicIfChanged();
        Assert.Single(hub.Sent);
    }

    private static HmiBroadcastService CreateService(
        out BridgeState values,
        out LogicStore logic,
        out FakeHubContext hub)
    {
        DeleteLogicFile();
        logic = new LogicStore(Options.Create(new BridgeOptions()));
        LogicBlockDto block = new()
        {
            Id = Guid.NewGuid(),
            Name = "Line 01 Start",
            Kind = LogicBlockKinds.Interlock,
            Conditions =
            {
                new LogicConditionDto
                {
                    Id = Guid.NewGuid(),
                    Text = "Line 01 start permit must be given",
                    SourceId = "sim",
                    ItemId = "Permit",
                    Op = LogicConditionOps.On
                }
            }
        };
        Assert.True(logic.TrySave(block, out _, out _, out _));

        values = new BridgeState(Options.Create(new BridgeOptions()));
        MappingStore mappings = new(Options.Create(new BridgeOptions()));
        hub = new FakeHubContext();
        return new HmiBroadcastService(values, mappings, logic, hub, Options.Create(new HmiOptions { BroadcastFlushMs = 1000 }));
    }

    private static void DeleteLogicFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "logic.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class FakeHubContext : IHubContext<HmiHub>
    {
        private readonly FakeClients clients_ = new();

        public List<(string Method, object? Payload)> Sent => clients_.Sent;

        public IHubClients Clients => clients_;

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeClients : IHubClients
    {
        public List<(string Method, object? Payload)> Sent { get; } = new();

        public IClientProxy All => new FakeProxy(Sent);

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Client(string connectionId) => throw new NotSupportedException();

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();

        public IClientProxy Group(string groupName) => throw new NotSupportedException();

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();

        public IClientProxy User(string userId) => throw new NotSupportedException();

        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private sealed class FakeProxy : IClientProxy
    {
        private readonly List<(string Method, object? Payload)> sent_;

        public FakeProxy(List<(string Method, object? Payload)> sent)
        {
            sent_ = sent;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            sent_.Add((method, args.Length > 0 ? args[0] : null));
            return Task.CompletedTask;
        }
    }
}
