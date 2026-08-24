using Microsoft.Extensions.Logging;
using OpcBridge.Core;
using OpcBridge.Da;

namespace OpcBridge.App;

public sealed record InterlinkDto(
    Guid Id,
    string ProviderSourceId,
    string ProviderItemId,
    string ConsumerSourceId,
    string ConsumerItemId,
    bool Enabled,
    short? ProviderCanonicalType,
    short? ConsumerCanonicalType,
    int? ProviderAccessRights = null,
    int? ConsumerAccessRights = null);

public sealed record CreateInterlinkRequest(InterlinkDto? Link);

public sealed record UpdateInterlinkRequest(InterlinkDto? Link);

public sealed record InterlinkTagMetadata(short? CanonicalType, int? AccessRights);

public interface IInterlinkMetadataResolver
{
    bool TryResolve(string sourceId, string itemId, out InterlinkTagMetadata metadata);
}


internal static class InterlinkValidators
{
    public static string? Validate(
        InterlinkDto link,
        bool consumerHasProvider)
    {
        if (link.ProviderAccessRights is null)
        {
            return "Provider tag not found.";
        }

        if (link.ConsumerAccessRights is null)
        {
            return "Consumer tag not found.";
        }

        return Validate(
            link,
            consumerHasProvider,
            providerReadable: IsReadable(link.ProviderAccessRights.Value),
            consumerWritable: IsWritable(link.ConsumerAccessRights.Value));
    }

    public static string? Validate(
        InterlinkDto link,
        bool consumerHasProvider,
        bool providerReadable,
        bool consumerWritable)
    {
        if (string.IsNullOrWhiteSpace(link.ProviderItemId))
        {
            return "Provider item is required.";
        }

        if (string.IsNullOrWhiteSpace(link.ConsumerItemId))
        {
            return "Consumer item is required.";
        }

        if (string.Equals(NormalizeSourceId(link.ProviderSourceId), NormalizeSourceId(link.ConsumerSourceId), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(link.ProviderItemId.Trim(), link.ConsumerItemId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "Provider and consumer cannot be the same tag.";
        }

        if (!providerReadable)
        {
            return "Provider tag must allow read.";
        }

        if (!consumerWritable)
        {
            return "Consumer tag must allow write.";
        }

        if (consumerHasProvider)
        {
            return "Consumer already has a provider.";
        }

        if (link.ProviderCanonicalType != link.ConsumerCanonicalType)
        {
            return "Provider and consumer must use the same data type.";
        }

        return null;
    }

    private static bool IsReadable(int accessRights)
    {
        return (accessRights & 1) != 0;
    }

    private static bool IsWritable(int accessRights)
    {
        return (accessRights & 2) != 0;
    }

    private static string NormalizeSourceId(string? sourceId)
    {
        string value = sourceId?.Trim() ?? string.Empty;
        return value.Length == 0 ? DaRuntimeSettings.DefaultSourceId : value;
    }
}

internal static class InterlinkApiHelpers
{
    /// <summary>
    /// Enforces the mapped-tags contract: an interlink only forwards values when
    /// both endpoints exist as enabled tags in Maps (the poller reads mapped tags
    /// and consumers are looked up in the mapping registry). Checked up front so
    /// dead links can never be created, even when no source server is connected.
    /// </summary>
    public static bool TryEnsureSidesAreMapped(
        IReadOnlyList<TagMapping> mappings,
        string providerSourceId,
        string providerItemId,
        string consumerSourceId,
        string consumerItemId,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        if (!IsMapped(mappings, providerSourceId, providerItemId))
        {
            error = "Provider tag must be added to Maps before linking.";
            return false;
        }

        if (!IsMapped(mappings, consumerSourceId, consumerItemId))
        {
            error = "Consumer tag must be added to Maps before linking.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsMapped(IReadOnlyList<TagMapping> mappings, string sourceId, string itemId)
    {
        string normalizedSourceId = sourceId?.Trim() ?? string.Empty;
        string normalizedItemId = itemId.Trim();
        return mappings.Any(mapping =>
            mapping.Enabled &&
            string.Equals(mapping.SourceId, normalizedSourceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(mapping.ItemId?.Trim(), normalizedItemId, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryMigrateLegacyInterlinks(
        InterlinkStore interlinkStore,
        IReadOnlyList<TagMapping> legacyMappings,
        DashboardLogStore logStore,
        ILogger logger,
        out string? warning)
    {
        ArgumentNullException.ThrowIfNull(interlinkStore);
        ArgumentNullException.ThrowIfNull(legacyMappings);
        ArgumentNullException.ThrowIfNull(logStore);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            interlinkStore.MigrateFromMappings(legacyMappings);
            warning = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            warning = $"Skipping legacy interlink migration from mappings.json because {ex.Message}";
            logStore.Add(LogLevel.Warning, "OpcBridge.App.InterlinkMigration", warning, ex);
            logger.LogWarning(ex, "Skipping legacy interlink migration from mappings.json because {Reason}", ex.Message);
            return false;
        }
    }

    public static bool TryGetStoredInterlinkRule(InterlinkStore linkStore, Guid id, out InterlinkRule? rule)
    {
        ArgumentNullException.ThrowIfNull(linkStore);

        (IReadOnlyList<InterlinkRule> rules, _) = linkStore.GetSnapshot();
        rule = rules.FirstOrDefault(existing => existing.Id == id);
        return rule is not null;
    }
}
