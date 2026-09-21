# ALCops MCP Server

An [MCP](https://modelcontextprotocol.io/) server that brings AL **code fixes** to AI assistants, and re-exposes Microsoft's own AL MCP tools alongside them. Lets Claude, Cursor, and other MCP clients compile Business Central AL projects, browse rules, and apply fixes — all without leaving the conversation.

## Install

```sh
dotnet tool install -g Microsoft.Dynamics.BusinessCentral.Development.Tools
dotnet tool install -g ALCops.Mcp
```

Alternatively, point `--devtools-path` or `BCDEVELOPMENTTOOLSPATH` at any directory containing the DevTools DLLs. Nothing is downloaded at runtime except ALCops' own analyzers (from NuGet); if no DevTools are found the server exits with the install command.

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

## Tools

**Native:** `list_rules`, `get_fixes`, `apply_fix`, `apply_fix_all`, `analyze` — code fixes, rule discovery, and structured diagnostics with filtering. `analyze` wraps `al_compile` (needs `almcp`); the others work without it.

**Proxied from `almcp`:** `al_compile`, `al_build`, `al_getdiagnostics`, `al_downloadsymbols`, `al_symbolsearch`, `al_publish`, `al_run_tests` and the rest of the `al_*` set. Pass `--no-proxy` to suppress these when your agent already registers `almcp` itself.

> **`al_compile` defaults to `onlyErrors: true`.** Nearly every ALCops rule is a *warning*, so pass `onlyErrors: false` or you will see no cop diagnostics at all. The native `analyze` tool does this for you.

## Analyzers

Microsoft cops and third-party analyzers are **not bundled** — the server loads exactly what your project configures via `al.codeAnalyzers` in `.vscode/settings.json`. AL-Go's `rulesetFile` and the `custom.ruleset.json` / `app.ruleset.json` conventions are honored too, and the same configuration is handed to the child `almcp` so `al_compile` and `get_fixes` agree about which rules run and which are suppressed.

ALCops' own analyzers are provisioned automatically at every startup: the server detects the installed DevTools target framework, downloads the matching `ALCops.Analyzers` NuGet package, and caches it under `~/.alcops/analyzers/`. Configure with `--alcops-analyzers` (`latest` | `prerelease` | `<version>` | `off`) or the `ALCOPS_ANALYZERS` environment variable.

Browse the ALCops rules reference at [alcops.dev/docs/analyzers](https://alcops.dev/docs/analyzers/).

## Links

- [Documentation](https://alcops.dev)
- [Source](https://github.com/ALCops/mcp-server)
- [Issues](https://github.com/ALCops/mcp-server/issues)
