# Releasing

This project uses [Semantic Versioning](https://semver.org/) and Git tags of the form `vMAJOR.MINOR.PATCH` (for example `v0.1.0`).

## What gets published

Each release includes two zip archives built by GitHub Actions:

| Archive | Contents |
|---|---|
| `TeklaMcp.Server-{version}-net48-win-x64.zip` | Windows executable + DLLs for live Tekla integration |
| `TeklaMcp.Server-{version}-net8.0-mock.zip` | Mock backend for development without Tekla |

The **net48 zip** is what most users need. It contains `TeklaMcp.Server.exe` and all dependent assemblies — they must stay in the same directory.

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

   Open **Actions → Release** on GitHub. When it completes, the release appears under **Releases** with both zip files attached.

Tags matching `v0.*` are marked as **pre-release** automatically.

---

## Option B — Manual release (upload exe yourself)

Use this if you built locally or the CI workflow is not set up yet.

### 1. Build locally

```powershell
dotnet build TeklaMcp.sln -c Release
```

Output folder:

```
src\TeklaMcp.Server\bin\Release\net48\
```

This folder contains `TeklaMcp.Server.exe` plus all required `.dll` files. **Upload the whole folder as a zip**, not just the exe — the server will not run without the DLLs.

### 2. Create the zip

```powershell
cd src\TeklaMcp.Server\bin\Release\net48
Compress-Archive -Path * -DestinationPath ..\..\..\..\..\..\TeklaMcp.Server-0.1.0-net48-win-x64.zip
```

Or select all files in the `net48` folder and compress them in Explorer.

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
- Tekla Open API assemblies (from NuGet)

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
- [ ] Tag pushed (`vX.Y.Z`)
- [ ] GitHub Release contains both zip artifacts
- [ ] Replace `YOUR_ORG` in CHANGELOG compare links with your GitHub username/org
