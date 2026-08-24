# ALCops MCP Server

An [MCP](https://modelcontextprotocol.io/) server that brings AL **code fixes** to AI assistants, and re-exposes Microsoft's own AL MCP tools alongside them. Lets Claude, Cursor, and other MCP clients compile Business Central AL projects, browse rules, and apply fixes — all without leaving the conversation.

## Install

```sh
dotnet tool install -g Microsoft.Dynamics.BusinessCentral.Development.Tools
dotnet tool install -g ALCops.Mcp
```

The first install is optional if you already have the [AL Language](https://marketplace.visualstudio.com/items?itemName=ms-dynamics-smb.al) VS Code extension — the server finds the BC Development Tools there instead. Nothing is downloaded at runtime; if neither is present the server exits with the install command.

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

**Native:** `list_rules`, `get_fixes`, `apply_fix`, `apply_fix_all` — code fixes and rule discovery, which Microsoft's `almcp` does not provide.

**Proxied from `almcp`:** `al_compile`, `al_build`, `al_getdiagnostics`, `al_downloadsymbols`, `al_symbolsearch`, `al_publish`, `al_run_tests` and the rest of the `al_*` set. Pass `--no-proxy` to suppress these when your agent already registers `almcp` itself.

> **`al_compile` defaults to `onlyErrors: true`.** Nearly every ALCops rule is a *warning*, so pass `onlyErrors: false` or you will see no cop diagnostics at all.

## Analyzers

Analyzers are **not bundled**. The server loads exactly what your project configures via `al.codeAnalyzers` in `.vscode/settings.json` — ALCops' cops, BC's standard cops (`${CodeCop}`, `${UICop}`, `${PerTenantExtensionCop}`, `${AppSourceCop}`), or any third-party analyzer. AL-Go's `rulesetFile` and the `custom.ruleset.json` / `app.ruleset.json` conventions are honored too, and the same configuration is handed to the child `almcp` so `al_compile` and `get_fixes` agree about which rules run and which are suppressed.

Browse the ALCops rules reference at [alcops.dev/docs/analyzers](https://alcops.dev/docs/analyzers/).

## Links

- [Documentation](https://alcops.dev)
- [Source](https://github.com/ALCops/mcp-server)
- [Issues](https://github.com/ALCops/mcp-server/issues)
