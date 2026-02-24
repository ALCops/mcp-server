using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Workspaces;
using Microsoft.Dynamics.Nav.CodeAnalysis.Text;

namespace ALCops.Mcp.Services;

public sealed class ProjectLoader
{
    private readonly DevToolsLocator _devToolsLocator;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ProjectLoader(DevToolsLocator devToolsLocator)
    {
        _devToolsLocator = devToolsLocator;
    }

    /// <summary>
    /// Loads an AL project from disk into a workspace with full compilation support.
    /// </summary>
    public async Task<ProjectSession> LoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        projectPath = Path.GetFullPath(projectPath);

        // 1. Load app.json
        var appJsonPath = Path.Combine(projectPath, "app.json");
        if (!File.Exists(appJsonPath))
            throw new FileNotFoundException($"No app.json found at {appJsonPath}");

        var appJson = await LoadAppJsonAsync(appJsonPath, ct);

        // 2. Enumerate .al files
        var alFiles = Directory.GetFiles(projectPath, "*.al", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".alpackages"))
            .ToArray();

        if (alFiles.Length == 0)
            throw new InvalidOperationException($"No .al files found in {projectPath}");

        // 3. Create workspace and project
        var workspace = new AlProjectWorkspace();
        var projectId = ProjectId.CreateNewId(appJson.Name);

        // 4. Build document infos from .al files
        var filePathToDocId = ImmutableDictionary.CreateBuilder<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
        var documentInfos = new List<DocumentInfo>();

        foreach (var alFile in alFiles)
        {
            var normalizedPath = Path.GetFullPath(alFile);
            var content = await File.ReadAllTextAsync(alFile, ct);
            var docId = DocumentId.CreateNewId(projectId, Path.GetFileName(alFile));

            var sourceText = SourceText.From(content);
            var loader = TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create(), normalizedPath));

            var docInfo = DocumentInfo.Create(
                docId,
                Path.GetFileName(alFile),
                loader: loader,
                filePath: normalizedPath);

            documentInfos.Add(docInfo);
            filePathToDocId[normalizedPath] = docId;
        }

        // 5. Resolve package cache paths (.alpackages directory)
        var packagePaths = new List<string>();
        var alPackagesDir = Path.Combine(projectPath, ".alpackages");
        if (Directory.Exists(alPackagesDir))
            packagePaths.Add(alPackagesDir);

        // Add DevTools path for system symbols
        try
        {
            var devToolsPath = _devToolsLocator.GetDevToolsPath();
            packagePaths.Add(Path.Combine(devToolsPath, "net8.0"));
        }
        catch
        {
            // DevTools not found — compilation will lack system symbols
            Console.Error.WriteLine("Warning: BC DevTools not found. Compilation will lack system symbols.");
        }

        // 6. Create ProjectInfo with packageCachePaths so the workspace resolves .app dependencies
        var projectInfo = ProjectInfo.Create(
            id: projectId,
            version: VersionStamp.Create(),
            name: appJson.Name,
            assemblyName: appJson.Name,
            language: LanguageNames.AL,
            filePath: appJsonPath,
            packageCachePaths: packagePaths,
            documents: documentInfos);

        // 7. Add project to workspace
        workspace.AddProject(projectInfo);

        return new ProjectSession(workspace, projectId, projectPath, filePathToDocId.ToImmutable());
    }

    private static async Task<AppJsonModel> LoadAppJsonAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AppJsonModel>(stream, JsonOptions, ct)
            ?? throw new InvalidOperationException($"Failed to deserialize {path}");
    }

    private sealed class AppJsonModel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("publisher")]
        public string Publisher { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [JsonPropertyName("runtime")]
        public string Runtime { get; set; } = "";

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "";

        [JsonPropertyName("application")]
        public string Application { get; set; } = "";
    }
}
