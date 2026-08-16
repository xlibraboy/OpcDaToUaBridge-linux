namespace OpcBridge.Drivers.Melsec.Addressing;

public enum MelsecDeviceKind
{
    D,
    M,
    X,
    Y,
    /// <summary>Timer contact (TS). Bit device, decimal numbering.</summary>
    TS,
    /// <summary>Timer coil (TC). Bit device, decimal numbering.</summary>
    TC,
    /// <summary>Timer present value (TN). Word device, decimal numbering.</summary>
    TN,
    /// <summary>Counter contact (CS). Bit device, decimal numbering.</summary>
    CS,
    /// <summary>Counter coil (CC). Bit device, decimal numbering.</summary>
    CC,
    /// <summary>Counter present value (CN). Word device, decimal numbering.</summary>
    CN
}
