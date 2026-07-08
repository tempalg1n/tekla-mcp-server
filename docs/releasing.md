# Releasing

This project uses [Semantic Versioning](https://semver.org/) and Git tags of the form `vMAJOR.MINOR.PATCH` (for example `v0.1.0`).

## What gets published

Each release includes **one zip per supported Tekla version** plus the mock build, all built by the GitHub Actions matrix ([release.yml](../.github/workflows/release.yml)):

| Archive | Compiled against (NuGet `TeklaVersion`) | For |
|---|---|---|
| `TeklaMcp.Server-vX.Y.Z-tekla2021.zip` | `2021.0.0` | Tekla Structures 2021 |
| `TeklaMcp.Server-vX.Y.Z-tekla2022.zip` | `2022.0.10715` | Tekla Structures 2022 |
| `TeklaMcp.Server-vX.Y.Z-tekla2023.zip` | `2023.0.1` | Tekla Structures 2023 |
| `TeklaMcp.Server-vX.Y.Z-tekla2024.zip` | `2024.0.4` | Tekla Structures 2024 |
| `TeklaMcp.Server-vX.Y.Z-tekla2025.zip` | `2025.0.0` | Tekla Structures 2025 |
| `TeklaMcp.Server-vX.Y.Z-tekla2026.zip` | `2026.0.3` | Tekla Structures 2026 |
| `TeklaMcp.Server-X.Y.Z-net8.0-mock.zip` | — | Mock backend, development without Tekla |

Users download the **zip matching their Tekla version** (the year in the name). The Open API remoting protocol is version-locked, so a build compiled for one Tekla only talks to that Tekla — a mismatched zip fails fast at runtime with a message naming the right one ([#11](https://github.com/tempalg1n/tekla-mcp-server/issues/11)). Each Tekla zip contains `TeklaMcp.Server.exe` and all dependent assemblies — they must stay in the same directory. The Tekla DLLs themselves are **not** bundled (not redistributable); they load from the locally installed Tekla.

When a new Tekla version ships, add a matrix entry (year + latest NuGet package version from https://api.nuget.org/v3-flatcontainer/tekla.structures.model/index.json) to **both** [release.yml](../.github/workflows/release.yml) and [ci.yml](../.github/workflows/ci.yml), and update this table.

---

## Option A — Automated release (recommended)

When you push a version tag, the [release workflow](../.github/workflows/release.yml) builds the project and attaches the zips to a GitHub Release.

### Steps

1. **Update the changelog**

   Move items from `[Unreleased]` to a new version section in [CHANGELOG.md](../CHANGELOG.md).

2. **Add release notes** (optional but recommended)

   Create `docs/releases/vX.Y.Z.md` — copy from the previous release and edit.  
   The workflow uses this file as the release description when the tag matches (e.g. tag `v0.1.0` → `docs/releases/v0.1.0.md`).

3. **Commit and push**

   ```bash
   git add CHANGELOG.md docs/releases/
   git commit -m "Prepare release v0.1.0"
   git push origin main
   ```

4. **Create and push the tag**

   ```bash
   git tag -a v0.1.0 -m "v0.1.0 — first public release"
   git push origin v0.1.0
   ```

5. **Wait for Actions**

   Open **Actions → Release** on GitHub. The matrix builds all Tekla versions in parallel; when every job completes, the release appears under **Releases** with all zip files attached (six Tekla zips + the mock zip).

Tags matching `v0.*` are marked as **pre-release** automatically.

---

## Option B — Manual release (upload exe yourself)

Use this if you built locally or the CI workflow is not set up yet.

### 1. Build locally — once per Tekla version

Each artifact is compiled for ONE Tekla version, so repeat the build+zip cycle for every version you want to ship (use the NuGet versions from the table above):

```powershell
dotnet build TeklaMcp.sln -c Release -p:TeklaVersion=2023.0.1
```

Output folder:

```
src\TeklaMcp.Server\bin\Release\net48\
```

This folder contains `TeklaMcp.Server.exe` plus all required `.dll` files. **Upload the whole folder as a zip**, not just the exe — the server will not run without the DLLs.

### 2. Create the zip

```powershell
cd src\TeklaMcp.Server\bin\Release\net48
Compress-Archive -Path * -DestinationPath ..\..\..\..\..\..\TeklaMcp.Server-v0.6.0-tekla2023.zip
```

Then `dotnet build … -p:TeklaVersion=2024.0.4` and zip again as `…-tekla2024.zip`, and so on. (Clean between builds or the previous version's `TeklaMcp.Tekla.dll` may be reused.)

### 3. Create the release on GitHub

1. Open your repository on GitHub
2. Go to **Releases → Draft a new release**
3. Click **Choose a tag**, type `v0.1.0`, select **Create new tag: v0.1.0 on publish**
4. Set **Release title** to `v0.1.0`
5. Paste the contents of [docs/releases/v0.1.0.md](releases/v0.1.0.md) into the description
6. Check **Set as a pre-release** (recommended for `0.x` versions)
7. Under **Attach binaries**, drag and drop your zip file
8. Click **Publish release**

### 4. Verify

Download the zip from the release page, extract it, and confirm:

```powershell
.\TeklaMcp.Server.exe
```

The process should start and wait on stdin (no output on stdout — that is normal for MCP stdio).

---

## Attaching only the exe — why that is not enough

`TeklaMcp.Server.exe` is a .NET Framework application that loads:

- `TeklaMcp.Core.dll`, `TeklaMcp.Mock.dll`, `TeklaMcp.Tekla.dll`
- MCP SDK and hosting libraries
- Tekla Open API assemblies (loaded at runtime from the locally installed Tekla — these are deliberately **not** in the zip)

If you upload **only** the `.exe`, users will get runtime errors about missing assemblies. Always ship the **entire `net48` output folder** (as a zip).

---

## Version numbering cheat sheet

| Change | Example |
|---|---|
| New MCP tool, new optional field | `0.1.0` → `0.2.0` |
| Bug fix, no API change | `0.1.0` → `0.1.1` |
| Breaking change to tool names or required parameters | `0.x` → `1.0.0` |

While the project is in `0.x`, treat every minor release as potentially breaking for MCP clients that hard-code tool schemas.

---

## Checklist before each release

- [ ] [CHANGELOG.md](../CHANGELOG.md) updated
- [ ] `docs/releases/vX.Y.Z.md` written
- [ ] README tool table still accurate
- [ ] `dotnet build TeklaMcp.sln -c Release` succeeds locally
  *(close running TeklaMcp.Server processes first)*
- [ ] New Tekla version released since last time? Add it to the release + CI matrices (see "What gets published")
- [ ] Tag pushed (`vX.Y.Z`)
- [ ] GitHub Release contains all Tekla zips + the mock zip
- [ ] Replace `YOUR_ORG` in CHANGELOG compare links with your GitHub username/org
