namespace EstreamAssistant.Core.Settings;

/// <summary>
/// Stores the Anthropic API key. The Windows implementation uses DPAPI
/// (CurrentUser). Never write the key in plain text or to a log.
/// </summary>
public interface ISecretStore
{
    string? GetApiKey();
    void SetApiKey(string apiKey);
    void ClearApiKey();
    bool HasApiKey { get; }
}

/// <summary>Non-persistent store for tests and non-Windows dev runs.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private string? _key;
    public string? GetApiKey() => _key;
    public void SetApiKey(string apiKey) => _key = apiKey;
    public void ClearApiKey() => _key = null;
    public bool HasApiKey => !string.IsNullOrEmpty(_key);
}

/// <summary>Masks a key for display: "sk-ant-...x7Qa".</summary>
public static class SecretMask
{
    public static string Mask(string? key) =>
        string.IsNullOrEmpty(key) ? "(not set)"
        : key.Length <= 12 ? new string('•', key.Length)
        : key[..7] + "…" + key[^4..];
}
