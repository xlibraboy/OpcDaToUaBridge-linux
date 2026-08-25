namespace OpcBridge.Core;

/// <summary>
/// One named PLC polling group on a PLC-type source: a display name and the update rate (ms)
/// its member tags are polled at. Pure data type — validation/clamping happens at the
/// settings/API layer (100 ms floor), mirroring <see cref="UaSubscriptionSettings"/>.
/// </summary>
public sealed record PlcGroupSettings(string Name, int UpdateRateMs);
