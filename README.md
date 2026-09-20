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

## Requirements

- [.NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0) SDK or Runtime
- BC Development Tools **v18.0 or higher** from the `Microsoft.Dynamics.BusinessCentral.Development.Tools` dotnet tool. Alternatively, point `--devtools-path` or `BCDEVELOPMENTTOOLSPATH` at any directory containing the DevTools DLLs (e.g. the AL VS Code extension's `bin/<platform>` folder).

> **Note:** v16 and earlier are not supported — the BC Development Tools DLLs introduced breaking API changes in v17. `almcp` also first shipped in v17, so on an older toolchain only the native tools below are available.

### Platform support

| OS | How `almcp` is launched | Notes |
|----|-------------------------|-------|
| Windows | Native `almcp.exe` | Ships in the nupkg. |
| Linux | `dotnet almcp.dll` | The nupkg has no extension-less launcher; the server falls back to the dotnet host automatically. |
| macOS | `dotnet almcp.dll` | Same as Linux. |

The native tools (`list_rules`, `get_fixes`, `apply_fix`, `apply_fix_all`) work on every OS regardless of `almcp` availability.

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

`almcp` is started in the background, so the native tools are available the moment the server comes up; the `al_*` tools appear once it has loaded your project (the server sends `tools/list_changed`, and hosts that don't support it see them on their next tool listing).

Pass `--no-proxy` to serve only the native tools. Use it when your agent already registers Microsoft's `almcp` itself, so the `al_*` tools don't show up twice.

> **`al_compile` defaults to `onlyErrors: true`.** Nearly every ALCops rule is a *warning*, so pass `onlyErrors: false` or you will see no cop diagnostics at all.

### Verifying a fix

After `apply_fix` or `apply_fix_all`, use `al_compile` with `onlyErrors: false` to confirm the diagnostic is gone. `al_compile` awaits almcp's internal file watcher, so it picks up the on-disk change reliably. Do **not** use `al_getdiagnostics` for this: it returns cached compilation results rather than re-analyzing, and will report stale (or empty) diagnostics. Only restarting the server gives a fully fresh almcp workspace.

## Analyzers

**Microsoft cops and third-party analyzers are never bundled.** The server loads exactly what your project configures via `al.codeAnalyzers` in `.vscode/settings.json` — BC's standard cops (`${CodeCop}`, `${UICop}`, `${PerTenantExtensionCop}`, `${AppSourceCop}`) and any third-party analyzer resolve from the DevTools directory. AL-Go's `rulesetFile` and the `custom.ruleset.json` / `app.ruleset.json` conventions are honored too.

This is deliberate: bundling pinned cop DLLs beside whatever `Nav.CodeAnalysis` you have installed is what produced `AD0001` / `MissingMethodException` failures ([#10](https://github.com/ALCops/mcp-server/issues/10)).

### ALCops analyzer provisioning

ALCops' own analyzers (`${analyzerFolder}ALCops.*.dll`) are provisioned automatically at every startup. The server detects the installed DevTools' target framework (e.g. `net10.0`), and on the first start downloads the latest stable [ALCops.Analyzers](https://www.nuget.org/packages/ALCops.Analyzers) NuGet package, extracts the matching `lib/<tfm>/` folder, and caches the DLLs under `~/.alcops/analyzers/<tfm>/<version>/`. On later starts the newest cached version is used immediately so `almcp` launches without waiting on NuGet; a NuGet check and any download run in the background and a newer version is used on the **next** start. Older cached versions are left in place.

Configure with `--alcops-analyzers` or the `ALCOPS_ANALYZERS` environment variable:

| Value | Behaviour |
|-------|-----------|
| `latest` (default) | Newest cached stable release; a newer one is fetched in the background for the next start (the first run downloads before starting). |
| `prerelease` | Highest version including prereleases from cache; a newer one is fetched in the background for the next start (the first run downloads before starting). |
| `<version>` (e.g. `1.2.0`) | Pin to a specific version (no index lookup). |
| `off` | Disable provisioning entirely. |

Set `ALCOPS_ANALYZERS_CACHE` to override the default cache directory (`~/.alcops/analyzers`).

When NuGet is unreachable or slow, the newest previously cached version for the target TFM is used with a warning. When no cache exists, the server starts without ALCops analyzers and logs a message with manual provisioning instructions.

The recommended `al.codeAnalyzers` configuration:

```json
{
  "al.codeAnalyzers": [
    "${CodeCop}",
    "${analyzerFolder}ALCops.LinterCop.dll"
  ]
}
```

The same analyzer, ruleset and `al.packageCachePath` configuration is passed to the child `almcp` at startup, so `al_compile` and `get_fixes` always agree about which rules run, which are suppressed, and where symbols come from.

Browse the ALCops rules reference at [alcops.dev/docs/analyzers](https://alcops.dev/docs/analyzers/).

## BC DevTools Resolution

The DevTools DLLs and `almcp` live in the same directory, so one lookup serves both. On startup the server searches, in order, and logs which one won:

1. `--devtools-path <dir>`
2. `BCDEVELOPMENTTOOLSPATH` environment variable
3. dotnet tool store (`~/.dotnet/tools/.store/…`) — highest version wins

The AL VS Code extension is no longer probed. If you use it as your only DevTools source, point `--devtools-path` at its `bin/<platform>` folder.

If none match, the server exits with the exact install command and a list of what was checked, rather than starting up degraded. The DevTools themselves are never downloaded at runtime — only ALCops' own analyzers are provisioned from NuGet (see [Analyzers](#analyzers) above).

## CLI Options

| Option | Description |
|--------|-------------|
| `--devtools-path <dir>` | Use this BC DevTools directory instead of probing. |
| `--projects <dir>[;<dir>]` | Work on these projects instead of scanning down from the working directory for `app.json`. A directory that is not itself a project is scanned for projects beneath it; entries with none are ignored with a warning. |
| `--no-proxy` | Serve only the native tools; do not start `almcp`. |
| `--alcops-analyzers <mode>` | `latest` (default), `prerelease`, a pinned version, or `off`. See [Analyzers](#analyzers). |

Arguments `almcp` understands — `--codeanalyzers`, `--rulesetpath`, `--settingspath`, `--enablecodeanalysis`, `--enableexternalrulesets`, `--locale`, `--noauth`, `--nolog` and friends — are forwarded to the child process and override anything discovered from your project.

## Contributing

Contributions are welcome! Whether it's a new tool, a bug report, or a pull request — all input helps.

- 🐛 **Report a bug** — File an [Issue](https://github.com/ALCops/mcp-server/issues/new)
- 🔧 **Submit a PR** — Fork the repo, create a branch, and open a pull request

## License

This project is licensed under the [MIT License](LICENSE).
