using InfluxDB.Client.Core.Exceptions;

namespace OpcBridge.Influx;

/// <summary>
/// Renders an InfluxDB client exception as the operator-facing text for <c>lastError</c> and the
/// durable log. The server's own message names the cause (field type conflict, bucket not found,
/// unauthorized); the HTTP status and a short recovery hint are added so the Historian panel says
/// what happened and what to do about it without digging through the server.
/// </summary>
public static class InfluxErrorDescriber
{
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is not InfluxException influx)
        {
            return ex.Message;
        }

        string message = string.IsNullOrWhiteSpace(influx.Message) ? influx.GetType().Name : influx.Message;
        string core = influx.Status > 0
            ? $"HTTP {influx.Status}: {message}"
            : $"No response from the server: {message}";
        return core + Hint(influx.Status, message);
    }

    private static string Hint(int status, string message)
    {
        if (message.Contains("field type conflict", StringComparison.OrdinalIgnoreCase))
        {
            return " — InfluxDB locks a field name to one type per measurement; point the writer at a new Measurement or bucket, or remove the old conflicting data.";
        }

        if (status == 404)
        {
            return " — check the Bucket and Org names on the InfluxDB tab.";
        }

        if (status is 401 or 403)
        {
            return " — check the Token on the InfluxDB tab and that it may write to the bucket.";
        }

        return string.Empty;
    }
}
