namespace OpcBridge.Drivers.S7.Protocol;

/// <summary>
/// S7 area codes used in PPI/S7comm variable-spec (libnodave daveInputs/Outputs/Flags/DB).
/// V memory on S7-200 is addressed as DB number 1.
/// </summary>
public static class PpiAreas
{
    public const byte Inputs = 0x81;
    public const byte Outputs = 0x82;
    public const byte Flags = 0x83;
    public const byte DB = 0x84;
}
