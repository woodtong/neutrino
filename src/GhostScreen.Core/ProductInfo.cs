using System.Reflection;

namespace GhostScreen.Core;

/// <summary>Single source of truth for the product name and version.</summary>
public static class ProductInfo
{
    public const string Name = "GhostScreen";

    /// <summary>
    /// Product version. Resolved from the assembly informational version so a
    /// release build can override it with <c>-p:Version=...</c> while
    /// development builds keep the value from <c>Directory.Build.props</c>.
    /// The source-revision suffix (<c>+&lt;commit&gt;</c>) is stripped.
    /// </summary>
    public static readonly string Version = ResolveVersion();

    private static string ResolveVersion()
    {
        var informational = typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informational)
            ? "0.0.0"
            : informational.Split('+')[0];
    }
}
