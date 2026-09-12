@AGENTS.md

## Claude Code notes

- Shell commands run through the PowerShell tool on Windows; the `dotnet build/test/pack/restore` commands in AGENTS.md are pre-allowed in `.claude/settings.json`.
- `.mcp.json` registers the published `alcops-mcp` tool (the same snippet the README gives users). It needs `dotnet tool install -g ALCops.Mcp` plus BC DevTools installed; it is not the dev build from `src/`.
