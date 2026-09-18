using OpcBridge.Hmi.Core;

namespace OpcBridge.Hmi.ViewModels;

/// <summary>
/// One row in a trend-group tag list: tag metadata only — deliberately no live value, quality
/// or timestamp — so lists that only pick or review tags stay stable while values stream in.
/// </summary>
/// <param name="Marker">Short right-hand note ("no history", "in group", "not on bridge"); empty hides it.</param>
/// <param name="Enabled">False greys the row out (no history, or already in the group).</param>
public sealed record TagListRow(
    TagBindingKey Key,
    string BridgeId,
    string SourceName,
    string DisplayName,
    string DaItemId,
    string Marker = "",
    bool Enabled = true)
{
    /// <summary>True when there is a note to show in the row's right-hand marker cell.</summary>
    public bool HasMarker => Marker.Length > 0;
}
