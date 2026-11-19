using System.Reflection;

namespace EstreamAssistant.App;

public static class AppVersion
{
    /// <summary>e.g. "0.1.0" (from &lt;Version&gt; in the csproj; the release build sets it from the git tag).</summary>
    public static string Display { get; } =
        (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
