namespace OpcBridge.Drivers.S7.Addressing;

public sealed record S7Address(
    S7Area Area,
    int ByteOffset,
    int SizeBytes,
    int? BitIndex,
    string Canonical);
