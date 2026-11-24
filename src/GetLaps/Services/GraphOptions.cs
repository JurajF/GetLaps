namespace GetLaps.Services;

public sealed class GraphOptions
{
    public const string SectionName = "Graph";

    /// <summary>Microsoft Graph base URL. Change only for national clouds.</summary>
    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0/";

    /// <summary>Graph beta base URL, used only for rotateLocalAdminPassword.</summary>
    public string BetaBaseUrl { get; set; } = "https://graph.microsoft.com/beta/";
}
