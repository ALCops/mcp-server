# ALCops MCP Server

[![NuGet](https://img.shields.io/nuget/v/ALCops.Mcp?logo=nuget&label=NuGet)](https://www.nuget.org/packages/ALCops.Mcp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ALCops.Mcp?logo=nuget&label=Downloads)](https://www.nuget.org/packages/ALCops.Mcp)
[![Build](https://img.shields.io/github/actions/workflow/status/ALCops/mcp-server/build-and-release.yml?logo=github&label=Build)](https://github.com/ALCops/mcp-server/actions)
[![License](https://img.shields.io/github/license/ALCops/mcp-server)](LICENSE)

An [MCP](https://modelcontextprotocol.io/) server that brings AL **code fixes** to AI assistants, and re-exposes Microsoft's own AL MCP tools alongside them. Lets Claude, Cursor, and other MCP clients compile Business Central AL projects, browse rules, and apply fixes — all without leaving the conversation.

Microsoft's `almcp` covers compiling, diagnostics, symbols, publishing, tests and translations, but has no code-fix capability at all (its LSP mode explicitly advertises `CodeActionProvider = false`) and no way to enumerate rules. Those two gaps are what this server adds; everything else it proxies straight through, so you get one endpoint instead of two.

## Quick Start

```sh
dotnet tool install -g Microsoft.Dynamics.BusinessCentral.Development.Tools
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

If you already have the [AL Language](https://marketplace.visualstudio.com/items?itemName=ms-dynamics-smb.al) VS Code extension, the first install is optional — the server finds the tools in the extension instead.

## Requirements

- [.NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) SDK or Runtime
- BC Development Tools **v17.0 or higher**, from either the `Microsoft.Dynamics.BusinessCentral.Development.Tools` dotnet tool or the [AL Language](https://marketplace.visualstudio.com/items?itemName=ms-dynamics-smb.al) VS Code extension

> **Note:** v16 and earlier are not supported — the BC Development Tools DLLs introduced breaking API changes in v17. `almcp` also first shipped in v17, so on an older toolchain only the native tools below are available.

## Tools

### Native (this server)

| Tool | Description |
|------|-------------|
| `list_rules` | List analyzer rules with metadata (ID, title, severity, category, cop). |
| `get_fixes` | Get available code fixes for a specific diagnostic at a location. |
| `apply_fix` | Apply a code fix to resolve a diagnostic. Writes the fixed content directly to the file on disk. |
| `apply_fix_all` | Apply a code fix to every occurrence of a diagnostic rule across a project or a single file (like VS Code's "Fix all in workspace"). Writes to disk unless `dryRun` is set. |

### Proxied from Microsoft's `almcp`

`al_compile`, `al_build`, `al_getdiagnostics`, `al_addproject`, `al_downloadsymbols`, `al_symbolsearch`, `al_symbolrelations`, `al_getpackagedependencies`, `al_inspectpage`, `al_publish`, `al_run_tests`, `al_searchtranslations`, `al_writetranslation`, `al_auth_login`, `al_auth_logout` — the exact set depends on your installed version.

Pass `--no-proxy` to serve only the native tools. Use it when your agent already registers Microsoft's `almcp` itself, so the `al_*` tools don't show up twice.

> **`al_compile` defaults to `onlyErrors: true`.** Nearly every ALCops rule is a *warning*, so pass `onlyErrors: false` or you will see no cop diagnostics at all.

## Analyzers

**Analyzers are not bundled.** The server loads exactly what your project configures via `al.codeAnalyzers` in `.vscode/settings.json` (AL-Go's `rulesetFile` and the `custom.ruleset.json` / `app.ruleset.json` conventions are honored too). That includes ALCops' cops, BC's standard cops (`${CodeCop}`, `${UICop}`, `${PerTenantExtensionCop}`, `${AppSourceCop}`), and any third-party analyzer.

This is deliberate: bundling pinned cop DLLs beside whatever `Nav.CodeAnalysis` you have installed is what produced `AD0001` / `MissingMethodException` failures ([#10](https://github.com/ALCops/mcp-server/issues/10)). Resolving both from your own toolchain makes that mismatch impossible.

```json
{
  "al.codeAnalyzers": [
    "${CodeCop}",
    "${analyzerFolder}ALCops.LinterCop.dll"
  ]
}
```

The same analyzer and ruleset configuration is passed to the child `almcp` at startup, so `al_compile` and `get_fixes` always agree about which rules run and which are suppressed.

Browse the ALCops rules reference at [alcops.dev/docs/analyzers](https://alcops.dev/docs/analyzers/).

## BC DevTools Resolution

The DevTools DLLs and `almcp` live in the same directory in both delivery channels, so one lookup serves both. On startup the server searches, in order, and logs which one won:

1. `--devtools-path <dir>`
2. `BCDEVELOPMENTTOOLSPATH` environment variable
3. dotnet tool store (`~/.dotnet/tools/.store/…`) — highest version wins
4. AL Language VS Code extension `bin/` — highest version wins

If none match, the server exits with the install command rather than starting up degraded. Nothing is downloaded at runtime.

## CLI Options

| Option | Description |
|--------|-------------|
| `--devtools-path <dir>` | Use this BC DevTools directory instead of probing. |
| `--projects <dir>[;<dir>]` | Work on these projects instead of scanning down from the working directory for `app.json`. |
| `--no-proxy` | Serve only the native tools; do not start `almcp`. |

Arguments `almcp` understands — `--codeanalyzers`, `--rulesetpath`, `--settingspath`, `--enablecodeanalysis`, `--enableexternalrulesets`, `--locale`, `--noauth`, `--nolog` and friends — are forwarded to the child process and override anything discovered from your project.

## Contributing

Contributions are welcome! Whether it's a new tool, a bug report, or a pull request — all input helps.

- 🐛 **Report a bug** — File an [Issue](https://github.com/ALCops/mcp-server/issues/new)
- 🔧 **Submit a PR** — Fork the repo, create a branch, and open a pull request

## License

This project is licensed under the [MIT License](LICENSE).
