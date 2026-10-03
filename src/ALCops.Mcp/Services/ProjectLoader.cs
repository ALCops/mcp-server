using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Workspaces;
using Microsoft.Dynamics.Nav.CodeAnalysis.Text;

namespace ALCops.Mcp.Services;

public sealed class ProjectLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string[] EnumerateAlFiles(string projectPath) =>
        Directory.GetFiles(projectPath, "*.al", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".alpackages"))
            .Select(Path.GetFullPath)
            .ToArray();

    internal const string TempFilePattern = "*" + GuardedFileWriter.TempSuffix;

    /// <summary>
    /// Deletes temp files a crashed <see cref="GuardedFileWriter"/> commit left behind under the
    /// project (outside <c>.alpackages</c>); never deletes anything else. Files younger than
    /// <paramref name="minAge"/> are kept because a concurrent write may be between creating its
    /// temp file and moving it over the target.
    /// </summary>
    /// <returns>The number of files removed.</returns>
    internal static int SweepTempFiles(string projectPath, TimeSpan minAge)
    {
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(projectPath, TempFilePattern, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: could not scan {projectPath} for stray temp files ({ex.GetType().Name}: {ex.Message}).");
            return 0;
        }

        var cutoff = DateTime.UtcNow - minAge;
        var removed = 0;

        foreach (var file in candidates.Where(f => !f.Contains(".alpackages")))
        {
            try
            {
                if (!file.EndsWith(GuardedFileWriter.TempSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (minAge > TimeSpan.Zero && File.GetLastWriteTimeUtc(file) > cutoff)
                    continue;

                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Warning: could not remove stray temp file {file} ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        if (removed > 0)
            Console.Error.WriteLine($"Removed {removed} stray *{GuardedFileWriter.TempSuffix} file(s) under {projectPath}.");

        return removed;
    }

    internal static DocumentInfo CreateDocumentInfo(ProjectId projectId, string normalizedPath, string content)
    {
        var docId = DocumentId.CreateNewId(projectId, Path.GetFileName(normalizedPath));
        var sourceText = SourceText.From(content);
        var loader = TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create(), normalizedPath));
        return DocumentInfo.Create(docId, Path.GetFileName(normalizedPath), loader: loader, filePath: normalizedPath);
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

        SweepTempFiles(projectPath, TimeSpan.Zero);

        var appJson = await LoadAppJsonAsync(appJsonPath, ct);

        // 2. Enumerate .al files
        var alFiles = EnumerateAlFiles(projectPath);

        if (alFiles.Length == 0)
            throw new InvalidOperationException($"No .al files found in {projectPath}");

        // 3. Create workspace and project
        var workspace = new AlProjectWorkspace();
        var projectId = ProjectId.CreateNewId(appJson.Name);

        // 4. Build document infos from .al files
        var documents = ImmutableDictionary.CreateBuilder<string, TrackedDocument>(StringComparer.OrdinalIgnoreCase);
        var documentInfos = new List<DocumentInfo>();

        foreach (var alFile in alFiles)
        {
            var normalizedPath = Path.GetFullPath(alFile);

            // Stamp BEFORE reading so a write landing between stamp and read is seen as newer on the next refresh.
            var stamp = FileStamp.Of(new FileInfo(normalizedPath));
            var content = await File.ReadAllTextAsync(alFile, ct);

            var docInfo = CreateDocumentInfo(projectId, normalizedPath, content);
            documentInfos.Add(docInfo);
            documents[normalizedPath] = new TrackedDocument(docInfo.Id, stamp);
        }

        // 5. Resolve package cache paths. Honour al.packageCachePath like the AL extension does —
        // multi-app repos routinely point every project at one shared cache — falling back to the
        // conventional .alpackages. Relative entries are relative to the project folder.
        var configured = ProjectAnalyzerResolver.GetConfiguredPackageCachePaths(projectPath) ?? [".alpackages"];
        var candidates = configured
            .Select(p => Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(projectPath, p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var packagePaths = candidates.Where(Directory.Exists).ToList();

        if (packagePaths.Count == 0)
        {
            // Not fatal — syntax-level cops still run — but symbol-dependent diagnostics will be
            // missing and this is the first place to look when get_fixes finds nothing.
            Console.Error.WriteLine(
                $"Warning: No package cache found for {projectPath} (looked in: {string.Join("; ", candidates)}). " +
                "Compilation will lack symbols; run al_downloadsymbols or check al.packageCachePath.");
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

        return new ProjectSession(workspace, projectId, projectPath, documents.ToImmutable());
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
