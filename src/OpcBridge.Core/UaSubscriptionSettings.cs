namespace OpcBridge.Core;

/// <summary>
/// One named OPC UA subscription definition on an OpcUa-type source: a display name
/// and the update rate (ms) used as both the subscription's PublishingInterval and
/// its member MonitoredItems' SamplingInterval. Pure data type — validation/clamping
/// happens at the settings/API layer (100 ms floor, see spec §4).
/// </summary>
public sealed record UaSubscriptionSettings(string Name, int UpdateRateMs);
