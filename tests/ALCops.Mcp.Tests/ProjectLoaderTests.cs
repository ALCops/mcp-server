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

    private static ProjectLoader CreateLoader()
    {
        // In CI, BCDEVELOPMENTTOOLSPATH is set to the downloaded BC DevTools.
        // Locally, DevToolsLocator resolves via the standard fallback chain.
        var locator = new DevToolsLocator();
        return new ProjectLoader(locator);
    }

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
