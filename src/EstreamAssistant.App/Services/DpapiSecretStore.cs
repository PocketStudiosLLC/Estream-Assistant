using System.Security.Cryptography;
using System.Text;
using EstreamAssistant.Core.Settings;

namespace EstreamAssistant.App.Services;

/// <summary>
/// Stores the Anthropic API key encrypted with DPAPI for the current Windows user
/// (Plan.txt sec. 10). Only the same Windows account on this PC can decrypt it.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    // Extra entropy so other apps using DPAPI for this user can't trivially read it.
    private static readonly byte[] Entropy = "eStreamAssistant.ApiKey.v1"u8.ToArray();
    private readonly string _path;

    public DpapiSecretStore(string path) => _path = path;

    public bool HasApiKey => File.Exists(_path);

    public string? GetApiKey()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            return null; // saved by another Windows user or corrupted
        }
    }

    public void SetApiKey(string apiKey)
    {
        var trimmed = apiKey.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("API key is empty.", nameof(apiKey));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(trimmed), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, cipher);
    }

    public void ClearApiKey()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
