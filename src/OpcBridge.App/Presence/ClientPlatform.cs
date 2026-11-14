namespace OpcBridge.App.Presence;

/// <summary>Turns a browser user agent into the OS and browser an operator recognizes.</summary>
internal static class ClientPlatform
{
    public static string Describe(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return string.Empty;
        }

        string os = DetectOs(userAgent);
        string browser = DetectBrowser(userAgent);
        return string.Join(" · ", new[] { os, browser }.Where(value => value.Length > 0));
    }

    private static string DetectOs(string userAgent)
    {
        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            string version = TokenVersion(userAgent, "Android ");
            return version.Length > 0 ? "Android " + version : "Android";
        }

        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase))
        {
            string family = userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPadOS" : "iOS";
            string version = TokenVersion(userAgent, "OS ").Replace('_', '.');
            return version.Length > 0 ? family + " " + version : family;
        }

        if (userAgent.Contains("Windows NT", StringComparison.OrdinalIgnoreCase))
        {
            string version = TokenVersion(userAgent, "Windows NT ");
            return version switch
            {
                "10.0" => "Windows 10/11",
                "6.3" => "Windows 8.1",
                "6.2" => "Windows 8",
                "6.1" => "Windows 7",
                _ => "Windows"
            };
        }

        if (userAgent.Contains("CrOS", StringComparison.OrdinalIgnoreCase))
        {
            return "ChromeOS";
        }

        if (userAgent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase))
        {
            string version = TokenVersion(userAgent, "Mac OS X ").Replace('_', '.');
            return version.Length > 0 ? "macOS " + version : "macOS";
        }

        if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return "Linux";
        }

        return string.Empty;
    }

    private static string DetectBrowser(string userAgent)
    {
        (string Token, string Name)[] browsers =
        {
            ("SamsungBrowser/", "Samsung Internet"),
            ("Edg/", "Edge"),
            ("OPR/", "Opera"),
            ("Firefox/", "Firefox"),
            ("Chrome/", "Chrome"),
            ("Version/", "Safari")
        };

        foreach ((string token, string name) in browsers)
        {
            string version = TokenVersion(userAgent, token);
            if (version.Length > 0)
            {
                // The major version is what an operator recognizes; the full build adds noise.
                return name + " " + version.Split('.')[0];
            }
        }

        return string.Empty;
    }

    private static string TokenVersion(string userAgent, string token)
    {
        int start = userAgent.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return string.Empty;
        }

        start += token.Length;
        int end = start;
        while (end < userAgent.Length && (char.IsDigit(userAgent[end]) || userAgent[end] == '.' || userAgent[end] == '_'))
        {
            end++;
        }

        return end > start ? userAgent[start..end].TrimEnd('.', '_') : string.Empty;
    }
}
