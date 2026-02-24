# ALCops MCP Server

[![NuGet](https://img.shields.io/nuget/v/ALCops.Mcp?logo=nuget&label=NuGet)](https://www.nuget.org/packages/ALCops.Mcp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ALCops.Mcp?logo=nuget&label=Downloads)](https://www.nuget.org/packages/ALCops.Mcp)
[![Build](https://img.shields.io/github/actions/workflow/status/ALCops/mcp-server/build-and-release.yml?logo=github&label=Build)](https://github.com/ALCops/mcp-server/actions)
[![License](https://img.shields.io/github/license/ALCops/mcp-server)](LICENSE)

An [MCP](https://modelcontextprotocol.io/) server that brings [ALCops](https://alcops.dev) AL code analysis to AI assistants. Lets Claude, Cursor, and other MCP clients analyze Business Central AL projects, browse rules, and apply code fixes — all without leaving the conversation.

## Quick Start

Install as a .NET global tool:

```sh
dotnet tool install -g ALCops.Mcp
```

Add to your `.mcp.json` (Claude Code) or `claude_desktop_config.json` (Claude Desktop):

```json
{
  "mcpServers": {
    "alcops": {
      "command": "alcops-mcp"
    }
  }
}
```

That's it. If you have the [AL Language](https://marketplace.visualstudio.com/items?itemName=ms-dynamics-smb.al) VS Code extension installed, the server picks up the BC Development Tools automatically.

## Tools

4 tools, ~1,020 tokens of schema overhead.

| Tool | Description |
|------|-------------|
| `analyze` | Run analyzers on an AL project or file. Returns diagnostics with severity, location, and code fix availability. |
| `list_rules` | List all available analyzer rules with metadata (ID, title, severity, category, cop). |
| `get_fixes` | Get available code fixes for a specific diagnostic at a location. |
| `apply_fix` | Apply a code fix and return the modified source — does **not** write to disk. |

## Analyzers

The server always includes ALCops' 6 built-in cops:

| Cop | Prefix | Description |
|-----|--------|-------------|
| [ApplicationCop](https://alcops.dev/docs/analyzers/applicationcop/) | AC | Correct modeling and behavior of BC objects |
| [DocumentationCop](https://alcops.dev/docs/analyzers/documentationcop/) | DC | Documentation quality and completeness |
| [FormattingCop](https://alcops.dev/docs/analyzers/formattingcop/) | FC | Stylistic and syntactic consistency |
| [LinterCop](https://alcops.dev/docs/analyzers/lintercop/) | LC | Code smells, maintainability, best practices |
| [PlatformCop](https://alcops.dev/docs/analyzers/platformcop/) | PC | AL language and runtime semantic correctness |
| [TestCop](https://alcops.dev/docs/analyzers/testcop/) | TC | Test codeunit structure and correctness |

Additionally, the server can load **BC standard analyzers** (`${CodeCop}`, `${UICop}`, `${PerTenantExtensionCop}`, `${AppSourceCop}`) and **third-party analyzers** — auto-discovered from `al.codeAnalyzers` in `.vscode/settings.json`, or passed explicitly via the `analyzers` tool parameter.

Browse the complete rules reference at [alcops.dev/docs/analyzers](https://alcops.dev/docs/analyzers/).

## BC DevTools Resolution

The server needs Microsoft BC Development Tools at runtime (not bundled due to licensing). On startup it searches, in order:

1. `BCDEVELOPMENTTOOLSPATH` environment variable
2. AL Language VS Code extension
3. Local cache (`~/.alcops/cache/devtools/`)
4. .NET global tools cache
5. Auto-download from NuGet (first run only)

Most users have the AL extension installed, so **no extra setup is needed**.

## Contributing

Contributions are welcome! Whether it's a new tool, a bug report, or a pull request — all input helps.

- 🐛 **Report a bug** — File an [Issue](https://github.com/ALCops/mcp-server/issues/new)
- 🔧 **Submit a PR** — Fork the repo, create a branch, and open a pull request

## License

This project is licensed under the [MIT License](LICENSE).
