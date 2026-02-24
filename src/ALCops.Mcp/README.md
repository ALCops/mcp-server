# ALCops MCP Server

An [MCP](https://modelcontextprotocol.io/) server that brings [ALCops](https://alcops.dev) AL code analysis to AI assistants. Lets Claude, Cursor, and other MCP clients analyze Business Central AL projects, browse rules, and apply code fixes — all without leaving the conversation.

## Install

```sh
dotnet tool install -g ALCops.Mcp
```

## Configure

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

If you have the [AL Language](https://marketplace.visualstudio.com/items?itemName=ms-dynamics-smb.al) VS Code extension installed, the server picks up BC Development Tools automatically. Otherwise set `BCDEVELOPMENTTOOLSPATH` or let the server auto-download them from NuGet on first run.

## Tools

4 tools, ~1,020 tokens of schema overhead.

| Tool | Description |
|------|-------------|
| `analyze` | Run analyzers on an AL project or file. Returns diagnostics with severity, location, and code fix availability. |
| `list_rules` | List all available analyzer rules with metadata (ID, title, severity, category, cop). |
| `get_fixes` | Get available code fixes for a specific diagnostic at a location. |
| `apply_fix` | Apply a code fix and return the modified source — does **not** write to disk. |

## Analyzers

Includes ALCops' 6 built-in cops (ApplicationCop, DocumentationCop, FormattingCop, LinterCop, PlatformCop, TestCop) plus optional BC standard analyzers (`${CodeCop}`, `${UICop}`, `${PerTenantExtensionCop}`, `${AppSourceCop}`) and third-party analyzers — auto-discovered from `al.codeAnalyzers` in `.vscode/settings.json`.

Browse the complete rules reference at [alcops.dev/docs/analyzers](https://alcops.dev/docs/analyzers/).

## Links

- [Documentation](https://alcops.dev)
- [Source](https://github.com/ALCops/mcp-server)
- [Issues](https://github.com/ALCops/mcp-server/issues)
