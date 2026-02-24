using System.Reflection;
using System.Runtime.Loader;

namespace ALCops.Mcp.Services;

/// <summary>
/// Custom AssemblyLoadContext for loading external analyzer DLLs.
/// Ensures shared types (DiagnosticAnalyzer, CodeFixProvider, etc.) are resolved
/// from the host (default) context so that typeof checks work correctly.
/// Without this, each cop DLL would load its own copy of Nav.CodeAnalysis,
/// causing type identity mismatches.
/// </summary>
internal sealed class AnalyzerAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _dllDirectory;

    public AnalyzerAssemblyLoadContext(string dllPath)
        : base(isCollectible: false)
    {
        _dllDirectory = Path.GetDirectoryName(dllPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Match by simple name (ignoring version/culture/public key token).
        // The BC cop DLLs reference Nav.CodeAnalysis with THEIR version number,
        // but we need to share the HOST's version so that typeof(DiagnosticAnalyzer)
        // from the host matches the base type in the cop DLL.
        var simpleName = assemblyName.Name;
        if (simpleName is not null)
        {
            foreach (var loaded in Default.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }
        }

        // Not in default context — probe the DLL's own directory
        var candidate = Path.Combine(_dllDirectory, assemblyName.Name + ".dll");
        if (File.Exists(candidate))
            return LoadFromAssemblyPath(candidate);

        return null;
    }
}
