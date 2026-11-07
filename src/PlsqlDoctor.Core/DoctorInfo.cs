using System.Reflection;

namespace PlsqlDoctor.Core;

public static class DoctorInfo
{
    public const string Name = "PL/SQL Performance Doctor";

    /// <summary>The package version from Directory.Build.props, without the "+commit" build metadata.</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(DoctorInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
