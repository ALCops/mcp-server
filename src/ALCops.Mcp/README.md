# ALCops MCP Server

An [MCP](https://modelcontextprotocol.io/) server that exposes ALCops AL code analyzers to AI assistants. Lets Claude (and other MCP clients) analyze Business Central AL projects, list rules, and apply code fixes — all without leaving the conversation.

## Tools

4 tools, ~1,020 tokens of tool schema overhead. All read-only.

| Tool | Description |
|------|-------------|
| `analyze` | Run analyzers on an AL project or file. Returns diagnostics with severity, location, and code fix availability. |
| `list_rules` | List all available analyzer rules with metadata (ID, title, severity, category, cop name). |
| `get_fixes` | Get available code fixes for a specific diagnostic at a location. |
| `apply_fix` | Apply a code fix and return the modified source — does **not** write to disk. |

## Analyzers

The server always includes ALCops' 6 built-in cops (ApplicationCop, DocumentationCop, FormattingCop, LinterCop, PlatformCop, TestAutomationCop) and can additionally load:

- **BC base analyzers** — `${CodeCop}`, `${UICop}`, `${PerTenantExtensionCop}`, `${AppSourceCop}`
- **Third-party analyzers** — `${analyzerFolder}SomeDll.dll` or absolute DLL paths

External analyzers are auto-discovered from `al.codeAnalyzers` in `.vscode/settings.json`, or passed explicitly via the `analyzers` tool parameter.

## Setup

### Install as a dotnet tool

```sh
dotnet tool install -g ALCops.Mcp
```

### Configure in Claude Code

Add to `.mcp.json` in your project root (or `~/.claude.json` globally):

```json
{
  "mcpServers": {
    "alcops": {
      "command": "alcops-mcp"
    }
  }
}
```

### Configure in Claude Desktop

Add to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "alcops": {
      "command": "alcops-mcp"
    }
  }
}
```

### BC DevTools Resolution

The server needs Microsoft BC Development Tools DLLs at runtime (not bundled due to licensing). On startup it searches, in order:

1. `BCDEVELOPMENTTOOLSPATH` environment variable
2. AL Language VS Code extension (`~/.vscode/extensions/ms-dynamics-smb.al-*/bin/`)
3. Local cache (`~/.alcops/cache/devtools/`)
4. .NET global tools cache (`~/.dotnet/tools/.store/microsoft.dynamics.businesscentral.development.tools.*`)
5. Auto-download from NuGet (first run only)
6. Relative to executable (local development)

Most users have the AL extension installed, so no extra setup is needed.

### Environment Variables

| Variable | Description |
|----------|-------------|
| `BCDEVELOPMENTTOOLSPATH` | Override BC DevTools location. Points to the root directory containing `net8.0/` subdirectory, or directly to a directory with `Microsoft.Dynamics.Nav.CodeAnalysis.dll`. |

### Build from source

```sh
dotnet build src/ALCops.Mcp/ALCops.Mcp.csproj --configuration Release
```

Requires .NET 8.0 SDK and BC Development Tools at `Microsoft.Dynamics.BusinessCentral.Development.Tools/` relative to the repo root.

## Usage Examples

### Analyze a project

```
analyze(projectPath: "/path/to/my-al-project")
```

### Analyze with BC base cops

```
analyze(
  projectPath: "/path/to/my-al-project",
  analyzers: '["${CodeCop}", "${UICop}"]'
)
```

If the project's `.vscode/settings.json` already has `al.codeAnalyzers` configured, the `analyzers` parameter can be omitted — the server auto-discovers them.

### List rules including external analyzers

```
list_rules(projectPath: "/path/to/my-al-project")
```

### Get and apply a fix

```
get_fixes(
  projectPath: "/path/to/my-al-project",
  filePath: "/path/to/my-al-project/src/MyCodeunit.Codeunit.al",
  diagnosticId: "AA0206",
  line: 15,
  column: 9
)

apply_fix(
  projectPath: "/path/to/my-al-project",
  filePath: "/path/to/my-al-project/src/MyCodeunit.Codeunit.al",
  diagnosticId: "AA0206",
  line: 15,
  column: 9,
  equivalenceKey: "FixVariableCasing"
)
```

## Architecture

```
Program.cs                         # Host setup, DI, MCP server registration
Tools/
  AnalyzeTool.cs                   # analyze
  ListRulesTool.cs                 # list_rules
  GetFixesTool.cs                  # get_fixes
  ApplyFixTool.cs                  # apply_fix
Services/
  IAnalyzerProvider.cs             # Shared interface for analyzer providers
  AnalyzerRegistry.cs              # Singleton — 6 built-in ALCops cops
  AnalyzerSet.cs                   # Per-request composite: built-in + external
  ExternalAnalyzerLoader.cs        # Loads/caches external DLLs
  ProjectAnalyzerResolver.cs       # Reads settings.json, builds AnalyzerSet
  AnalyzerSpec.cs                  # Parses analyzer spec strings
  LoadedAnalyzerAssembly.cs        # Loaded DLL result record
  ProjectLoader.cs                 # Creates AL compilations from project files
  ProjectSession.cs                # Holds a loaded project's compilation state
  ProjectSessionManager.cs         # Caches project sessions
  DiagnosticsRunner.cs             # Runs analyzers against a compilation
  CodeFixRunner.cs                 # Finds and applies code fixes
  DevToolsLocator.cs               # Resolves BC DevTools path
```

The server uses stdio transport (JSON-RPC over stdin/stdout). Logging goes to stderr only.
