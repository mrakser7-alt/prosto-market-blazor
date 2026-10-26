namespace SimpleCatalog.Services;

public class SupabaseOptions
{
    public string Url { get; set; } = string.Empty;
    public string PublishableKey { get; set; } = string.Empty;

    public bool IsConfigured =>
        Uri.TryCreate(Url, UriKind.Absolute, out _) &&
        !string.IsNullOrWhiteSpace(PublishableKey);
}
