# Copilot Instructions

## Build

```sh
# Build (BC DevTools come from restore, see below)
dotnet build --configuration Release

# Run tests
dotnet test --configuration Release

# Pack as .NET global tool
dotnet pack src/ALCops.Mcp/ALCops.Mcp.csproj --configuration Release --output ./artifacts
```

There are no linters in this repository.

### BC DevTools dependency

The project compiles against proprietary Microsoft BC Development Tools DLLs (`Microsoft.Dynamics.Nav.CodeAnalysis`, `.Workspaces`, `.Analyzers.Common`).

A plain `PackageReference` is impossible: as of 17.0 the `Microsoft.Dynamics.BusinessCentral.Development.Tools` package is `DotnetTool` + `Template` only, with all payload under `tools/<tfm>/any/`, and NuGet rejects referencing a `DotnetTool` package. Instead both csproj files declare a `<PackageDownload>` on a pinned version — which restores the nupkg into the global packages folder without referencing it — and point three `<Reference>` items at `$(NuGetPackageRoot)…/tools/$(BcToolsTfm)/any/`.

- `$(BcDevToolsVersion)` — the compile floor, deliberately the *lowest* supported stable release. Compiling against the oldest SDK and running against newer ones is what makes forward compatibility hold; CI overrides this property to run its version matrix.
- `$(BcToolsTfm)` — `net8.0` by default (the 17.x line ships net8.0 only; 18.x ships both).
- `src` sets `<Private>false</Private>`, so the proprietary DLLs never enter the build output and therefore never enter the published package. This is the redistribution guard.
- `tests` sets `<Private>true</Private>` on purpose: the CI compatibility matrix hot-swaps those three DLLs in the prebuilt test binary to run the same tests against every supported SDK version.

At runtime `BcToolsLocator` finds the DLLs in the user's own toolchain. Nothing is downloaded at runtime.

## Architecture

An MCP (Model Context Protocol) server packaged as a .NET 10 global tool (`alcops-mcp`), served over stdio JSON-RPC.

It is deliberately **thin**: Microsoft's `almcp` already compiles, runs diagnostics, resolves symbols, publishes, runs tests and handles translations, so all of that is proxied. What `almcp` has no capability for at all — code fixes (its LSP mode advertises `CodeActionProvider = false`) and rule enumeration — is what this server implements natively. Adding anything here that `almcp` already does is a regression of that design.

### Startup sequence (order matters)

1. `Program.cs` calls `BcToolsLocator.ResolveAndRegister()` to find the tools directory and register an `AssemblyLoadContext` resolver. This **must** happen before any BC types are JIT-compiled — the DLLs are not in the output directory, so nothing can resolve them before this runs.
2. `McpHost.RunAsync()` is marked `[NoInlining]` to enforce that ordering, then builds the host, registers DI services, and starts the MCP stdio transport.
3. `AlMcpProxyStartup` (an `IHostedService`) launches `almcp` as a child process on a free localhost port and caches its tool list.

### Key layers

- **Tools/** — MCP tool endpoints, annotated `[McpServerToolType]` / `[McpServerTool]`, auto-discovered via `WithToolsFromAssembly()`. Four native tools: `list_rules`, `get_fixes`, `apply_fix`, `apply_fix_all`. The proxied `al_*` tools are served by the dynamic list/call handlers in `McpHost`, not by classes here.
- **Services/**, all singletons registered in `McpHost`:
  - `BcToolsLocator` — the single runtime lookup. Finds the one directory holding both `Microsoft.Dynamics.Nav.*.dll` and `almcp[.exe]` (they ship side by side in both delivery channels). Probe order: `--devtools-path` → `BCDEVELOPMENTTOOLSPATH` → dotnet tool store → AL VS Code extension `bin/` → hard error naming the install command.
  - `WorkspaceStartupResolver` — discovers AL projects (mirrors `almcp`'s own `DiscoverProjectPaths`: downward scan for `app.json`, depth 4, standard exclusions) and composes the child `almcp`'s `--projects` / `--codeanalyzers` / `--rulesetpath` args. `almcp` in MCP mode never reads `.vscode/settings.json` and has no per-call analyzer or ruleset parameter, so this bridge at launch is the only thing keeping `al_compile` and our fix tools in agreement.
  - `AlMcpProxy` — child process lifecycle plus generic tool forwarding. `ForwardAsync` is a passthrough with **no per-tool argument rewriting**; configuration is conveyed at launch instead.
  - `ProjectAnalyzerResolver` — reads `al.codeAnalyzers` and the ruleset (`.vscode/settings.json`, `.AL-Go/settings.json`, convention-named files) and builds an `AnalyzerSet`. Nothing is built in.
  - `ExternalAnalyzerLoader` — loads analyzer DLLs through `AnalyzerAssemblyLoadContext`, which resolves shared types by simple name from the default context. That type sharing is what makes `typeof(DiagnosticAnalyzer).IsAssignableFrom` work, and therefore what makes in-process code fixes possible at all.
  - `ProjectSessionManager` / `ProjectLoader` — caches AL project workspaces keyed by path; `GetOrLoadProjectAsync` is the entry point tools use.
- **Models/** — record types for tool return values, serialized with `JsonDefaults.Options` (camelCase, not indented).

### Analyzers are never bundled

Shipping pinned cop DLLs beside whatever `Nav.CodeAnalysis` the user installed is what caused `AD0001` / `MissingMethodException` (issue #10). Analyzers come solely from the project's own config. `ALCops.Analyzers` is referenced by the **test project only**, so the fixtures have real cops with real code fixes to exercise; it must never move back to `src`.

When passing analyzers to the child `almcp`, their sibling dependencies must travel with them (`ALCops.Common.dll`, `Microsoft.Dynamics.Nav.Analyzers.Common.dll`): `almcp` resolves analyzer dependencies only among the paths it was given and does not probe the analyzer's directory. A missing one turns every rule in that assembly into an `AD0001` instead of a diagnostic.

### Tool patterns

- All tool methods are `static async Task<string>`, receiving DI services as parameters.
- Tools return JSON-serialized results. Errors are caught and returned as `{ error, message }` JSON, not thrown.
- `apply_fix` writes to disk and reloads the project session; `apply_fix_all` does the same across every occurrence of a rule (unless `dryRun`); the other two are read-only.
- `al_compile` defaults to `onlyErrors: true` while nearly every ALCops rule is a warning — callers must pass `onlyErrors: false`. This is documented rather than patched, because `ForwardAsync` stays a generic passthrough.

## Conventions

- **Target framework**: .NET 10, C# latest, nullable enabled, implicit usings.
- **Namespaces**: `ALCops.Mcp.Tools`, `ALCops.Mcp.Services`, `ALCops.Mcp.Models`. File-scoped namespaces throughout.
- **JSON serialization**: Use `JsonDefaults.Options` (camelCase) for tool responses. Use `JsonDocumentOptions` with `CommentHandling.Skip` and `AllowTrailingCommas` when parsing user-facing JSON files (settings.json, rulesets).
- **Logging**: All diagnostic output goes to stderr. Stdout is reserved for the MCP JSON-RPC protocol. Startup logs which tools directory won, which `settings.json` was read, and every resolved analyzer and ruleset path — a silently wrong working-directory guess is the failure mode this exists to make visible.
- **Versioning**: GitVersion with GitHubFlow. Version is determined from git history, not hardcoded. Branches: `main` produces alpha prereleases, `release/**` branches produce stable versions.
