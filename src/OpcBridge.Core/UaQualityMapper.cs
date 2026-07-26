namespace OpcBridge.Core;

public static class UaQualityMapper
{
    // Maps UA status code bits to a DA-like quality int + IsGood.
    // Good (0) → quality 0xC0, isGood true
    // Uncertain → quality 0x40, isGood false
    // Bad → quality 0x00, isGood false
    public static (int DaQuality, bool IsGood) FromStatusCode(uint statusCode)
    {
        // OPC UA StatusCode severity is in the top 2 bits (bits 30–31).
        // 00 = Good, 01 = Uncertain, 10/11 = Bad.
        var severity = statusCode >> 30;
        return severity switch
        {
            0 => (0xC0, true),
            1 => (0x40, false),
            _ => (0x00, false)
        };
    }
}
