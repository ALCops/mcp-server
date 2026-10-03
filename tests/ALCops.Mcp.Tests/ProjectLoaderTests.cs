using ALCops.Mcp.Services;
using Xunit;

namespace ALCops.Mcp.Tests;

public class ProjectLoaderTests
{
    private static string GetFixturePath(string name)
    {
        // Fixtures are copied to output directory via CopyToOutputDirectory
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"Test fixture '{name}' not found at {path}. Ensure fixtures are copied to output.");
        return path;
    }

    private static ProjectLoader CreateLoader() => new();

    [Fact]
    public async Task LoadProjectAsync_WithValidProject_ReturnsProjectSession()
    {
        // This test validates that ProjectInfo.Create() succeeds with the current BC DevTools version.
        // It will surface MissingMethodException if the ProjectInfo.Create signature has changed (issue #1).
        var loader = CreateLoader();
        var projectPath = GetFixturePath("MinimalProject");

        var session = await loader.LoadProjectAsync(projectPath);

        Assert.NotNull(session);
        Assert.NotNull(session.ProjectPath);
        Assert.NotEqual(default, session.ProjectId);
    }

    [Fact]
    public async Task LoadProjectAsync_HonoursPackageCachePathSetting()
    {
        // Multi-app repos routinely share one symbol cache via al.packageCachePath. The AL extension
        // resolves relative entries against the project folder; so must we, or get_fixes compiles
        // without symbols while the developer's editor is perfectly happy.
        var root = Path.Combine(Path.GetTempPath(), $"alcops-test-{Guid.NewGuid():N}");
        try
        {
            var project = Path.Combine(root, "App");
            var shared = Path.Combine(root, "shared-packages");
            TestAnalyzers.CopyDirectory(GetFixturePath("MinimalProject"), project);
            Directory.CreateDirectory(shared);
            Directory.CreateDirectory(Path.Combine(project, ".vscode"));
            await File.WriteAllTextAsync(
                Path.Combine(project, ".vscode", "settings.json"),
                """{ "al.packageCachePath": ["../shared-packages", "does-not-exist"] }""");

            var session = await CreateLoader().LoadProjectAsync(project);

            // Only directories that exist are handed to the workspace; the missing one is dropped.
            Assert.Equal([shared], session.GetProject().PackageCachePaths);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadProjectAsync_WithoutSetting_UsesAlPackages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alcops-test-{Guid.NewGuid():N}");
        try
        {
            TestAnalyzers.CopyDirectory(GetFixturePath("MinimalProject"), root);
            var alPackages = Path.Combine(root, ".alpackages");
            Directory.CreateDirectory(alPackages);

            var session = await CreateLoader().LoadProjectAsync(root);

            Assert.Equal([alPackages], session.GetProject().PackageCachePaths);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadProjectAsync_WithMissingAppJson_ThrowsFileNotFoundException()
    {
        var loader = CreateLoader();
        var tempDir = Path.Combine(Path.GetTempPath(), $"alcops-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);

            await Assert.ThrowsAsync<FileNotFoundException>(
                () => loader.LoadProjectAsync(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void EnumerateAlFiles_ExcludesAlPackages_ReturnsFullPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alcops-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Page.al"), "// page");
            var alPackages = Path.Combine(root, ".alpackages");
            Directory.CreateDirectory(alPackages);
            File.WriteAllText(Path.Combine(alPackages, "Dep.al"), "// dependency");
            File.WriteAllText(Path.Combine(root, "Page.al.x" + GuardedFileWriter.TempSuffix), "// temp");

            var files = ProjectLoader.EnumerateAlFiles(root);

            Assert.Single(files);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "Page.al")), files[0]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadProjectAsync_SweepsStrayTempFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alcops-test-{Guid.NewGuid():N}");
        try
        {
            TestAnalyzers.CopyDirectory(GetFixturePath("MinimalProject"), root);
            var alFile = Directory.GetFiles(root, "*.al").First();
            var stray = GuardedFileWriter.TempPathFor(alFile);
            File.WriteAllText(stray, "// left behind by a crashed write");
            var alPackages = Path.Combine(root, ".alpackages");
            Directory.CreateDirectory(alPackages);
            var dependencyTemp = Path.Combine(alPackages, "Dep.al.x" + GuardedFileWriter.TempSuffix);
            File.WriteAllText(dependencyTemp, "// not ours to touch");

            var session = await CreateLoader().LoadProjectAsync(root);

            Assert.False(File.Exists(stray));
            Assert.True(File.Exists(dependencyTemp));
            Assert.DoesNotContain(session.FilePathToDocumentId.Keys, k => k.EndsWith(GuardedFileWriter.TempSuffix));
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void SweepTempFiles_IgnoresAlPackages_ReturnsCount()
    {
        var root = Path.Combine(Path.GetTempPath(), $"alcops-test-{Guid.NewGuid():N}");
        try
        {
            var sub = Path.Combine(root, "src", "pages");
            Directory.CreateDirectory(sub);
            var alPackages = Path.Combine(root, ".alpackages");
            Directory.CreateDirectory(alPackages);

            File.WriteAllText(Path.Combine(root, "A.al"), "// a");
            File.WriteAllText(Path.Combine(root, "A.al.1" + GuardedFileWriter.TempSuffix), "");
            File.WriteAllText(Path.Combine(sub, "B.al.2" + GuardedFileWriter.TempSuffix), "");
            File.WriteAllText(Path.Combine(root, "unrelated.tmp"), "");
            var dependencyTemp = Path.Combine(alPackages, "C.al.3" + GuardedFileWriter.TempSuffix);
            File.WriteAllText(dependencyTemp, "");

            Assert.Equal(2, ProjectLoader.SweepTempFiles(root, TimeSpan.Zero));

            Assert.True(File.Exists(Path.Combine(root, "A.al")));
            Assert.True(File.Exists(Path.Combine(root, "unrelated.tmp")));
            Assert.True(File.Exists(dependencyTemp));
            Assert.Empty(Directory.GetFiles(sub));
            Assert.Equal(0, ProjectLoader.SweepTempFiles(root, TimeSpan.Zero));
        }
        finally
        {
            TestAnalyzers.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task LoadProjectAsync_WithNoAlFiles_ThrowsInvalidOperationException()
    {
        var loader = CreateLoader();
        var tempDir = Path.Combine(Path.GetTempPath(), $"alcops-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(
                Path.Combine(tempDir, "app.json"),
                """
                {
                    "id": "00000000-0000-0000-0000-000000000099",
                    "name": "EmptyProject",
                    "publisher": "Test",
                    "version": "1.0.0.0"
                }
                """);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => loader.LoadProjectAsync(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
