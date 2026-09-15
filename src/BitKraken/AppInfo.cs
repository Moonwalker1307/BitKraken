using System.Reflection;

namespace BitKraken;

/// <summary>Version of the running build, read from the assembly the packaging scripts stamp.</summary>
public static class AppInfo
{
    /// <summary>The product name, as shown in the macOS menu bar.</summary>
    public const string Name = "BitKraken";

    /// <summary>e.g. "1.0.3", or "1.0.3-preview" for a build off a pull request.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>The version as shown in the title bar, e.g. "v1.0.3".</summary>
    public static string DisplayVersion { get; } = "v" + Version;

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;

        // InformationalVersion is the full string the build stamped, pre-release suffix included.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return TrimBuildMetadata(informational);

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Drops the "+&lt;commit&gt;" build metadata the SDK can append to an informational version, which is
    /// noise in the UI. The pre-release suffix ("-preview") is part of the version and is kept.
    /// </summary>
    internal static string TrimBuildMetadata(string informational)
    {
        var metadata = informational.IndexOf('+');
        return metadata >= 0 ? informational[..metadata] : informational;
    }
}
