# Copilot Instructions

## Build

```sh
# Build (requires BC DevTools DLLs, see below)
dotnet build --configuration Release

# Run tests
dotnet test --configuration Release

# Pack as .NET global tool
dotnet pack src/ALCops.Mcp/ALCops.Mcp.csproj --configuration Release --output ./artifacts
```

There are no linters in this repository.

### BC DevTools dependency

The project references proprietary Microsoft BC Development Tools DLLs (`Microsoft.Dynamics.Nav.CodeAnalysis`, `.Workspaces`, `.Analyzers.Common`). These are **not bundled** and must exist locally for compilation. The `BcDevToolsDir` MSBuild property (default: `../../Microsoft.Dynamics.BusinessCentral.Development.Tools`) points to the directory containing a `net8.0/` subfolder with these DLLs.

At runtime, `BcDevToolsBootstrap` resolves them automatically through a fallback chain: `BCDEVELOPMENTTOOLSPATH` env var → AL VS Code extension → NuGet cache → auto-download from NuGet.

## Architecture

This is an MCP (Model Context Protocol) server packaged as a .NET 8 global tool (`alcops-mcp`). It exposes AL code analysis capabilities over stdio JSON-RPC.

### Startup sequence (order matters)

1. `Program.cs` calls `BcDevToolsBootstrap.ResolveAndRegister()` to find BC DevTools and register an `AssemblyLoadContext` resolver. This **must** happen before any BC types are JIT-compiled.
2. `McpHost.RunAsync()` is marked `[NoInlining]` to enforce this ordering, then builds the host, registers DI services, and starts the MCP stdio transport.

### Key layers

- **Tools/** — MCP tool endpoints. Each class is annotated with `[McpServerToolType]` and methods with `[McpServerTool]`. Tools are auto-discovered via `WithToolsFromAssembly()`. The four tools: `analyze`, `list_rules`, `get_fixes`, `apply_fix`.
- **Services/** — Core logic. Services are registered as singletons in `McpHost`.
  - `ProjectSessionManager` — Caches loaded AL project workspaces keyed by path. `GetOrLoadProjectAsync` is the main entry point tools use.
  - `AnalyzerRegistry` — Loads ALCops' 6 built-in cops at startup via reflection. Indexes analyzers, code fix providers, and diagnostic descriptors.
  - `ProjectAnalyzerResolver` — Merges built-in analyzers with external ones (BC standard cops like `${CodeCop}`, third-party DLLs). Reads analyzer config from `.vscode/settings.json` (`al.codeAnalyzers`). Also loads rulesets from multiple sources (`.vscode/settings.json`, `.AL-Go/settings.json`, convention-named files).
  - `ExternalAnalyzerLoader` — Loads third-party analyzer DLLs into isolated `AssemblyLoadContext` instances.
  - `ProjectLoader` — Creates an `AlProjectWorkspace` from disk (reads `app.json`, enumerates `.al` files, resolves `.alpackages`).
- **Models/** — Simple record types for tool return values. Serialized with `JsonDefaults.Options` (camelCase, not indented).

### Tool patterns

- All tool methods are `static async Task<string>`, receiving DI services as parameters.
- Tools return JSON-serialized results. Errors are caught and returned as `{ error, message }` JSON, not thrown.
- `apply_fix` writes to disk and reloads the project session; the other three tools are read-only.

## Conventions

- **Target framework**: .NET 8, C# latest, nullable enabled, implicit usings.
- **Namespaces**: `ALCops.Mcp.Tools`, `ALCops.Mcp.Services`, `ALCops.Mcp.Models`. File-scoped namespaces throughout.
- **JSON serialization**: Use `JsonDefaults.Options` (camelCase) for tool responses. Use `JsonDocumentOptions` with `CommentHandling.Skip` and `AllowTrailingCommas` when parsing user-facing JSON files (settings.json, rulesets).
- **Logging**: All diagnostic output goes to stderr (`Console.Error.WriteLine`). Stdout is reserved for the MCP JSON-RPC protocol.
- **Versioning**: GitVersion with GitHubFlow. Version is determined from git history, not hardcoded. Branches: `main` produces alpha prereleases, `release/**` branches produce stable versions.
- **NuGet packaging**: The project packs as a dotnet tool. BC DevTools DLLs are explicitly excluded from the package via the `ExcludeBcDevToolsFromPublish` MSBuild target.
