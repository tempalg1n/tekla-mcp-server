# Tekla MCP plugin card for Codex

Codex starts MCP servers listed in its `config.toml`, but only plugins get a card in its Plugins
directory. `Install-CodexPlugin.ps1` builds a **Tekla MCP** card for this computer: one MCP server
`tekla<year>` per installed Tekla version, each with its own switch.

1. Unpack the release zips for your Tekla versions next to each other, e.g.
   `C:\MCP\TeklaMcp.Server-v0.8.1-tekla2021` and `C:\MCP\TeklaMcp.Server-v0.8.1-tekla2026`.
2. Run the script from the `codex` folder of one of them:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File C:\MCP\TeklaMcp.Server-v0.8.1-tekla2026\codex\Install-CodexPlugin.ps1
   ```

3. Quit the Codex app completely (also from the system tray), start it again and open a new thread.

Run it again after unpacking a new release. Options: `-Years 2021,2026`, `-Server <exe>`,
`-ServerRoot <folder>`, `-FileRoot <folders>`, `-UseMock`, `-NoInstall`, `-RemoveConflicts`
(`Get-Help .\Install-CodexPlugin.ps1 -Detailed`).

Details, switches and troubleshooting: the "Codex (plugin card)" section of the project README,
https://github.com/tempalg1n/tekla-mcp-server#codex-plugin-card
