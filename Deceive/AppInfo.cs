using System.Reflection;

namespace Deceive;

/// <summary>
/// The app's display name, taken from the &lt;Product&gt; set in the .csproj (AppName property),
/// so the same code can be built under different names.
/// </summary>
internal static class AppInfo
{
    public static readonly string Name =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product is { Length: > 0 } product
            ? product
            : "Deceive";
}
