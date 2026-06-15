# Lidarr Plugin Release Reconciler

Release Reconciler is a Lidarr plugin that captures release mismatch evidence, evaluates candidate releases, and offers guarded operator-approved switching flows.

## Build

This repository uses the Lidarr source tree as a submodule under `ext/Lidarr`.

```bash
git submodule update --init --recursive
./scripts/prepare-lidarr-3x-host-compat.sh
dotnet build Lidarr.Plugin.ReleaseReconciler.csproj -c Release /p:NuGetAudit=false /p:RunAnalyzers=false /p:RunAnalyzersDuringBuild=false /p:EnableNETAnalyzers=false /p:EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false /p:NoWarn=SA1200%3BNU1902
```

## Install

Copy the built plugin files into:

```
/mnt/user/appdata/lidarr/plugins/jtstothard/Lidarr.Plugin.ReleaseReconciler/
```

At minimum, deploy the release zip contents there and restart Lidarr.


## Compatibility Note

This plugin is built against a patched Lidarr source submodule that emits `AssemblyVersion` `3.0.0.*` for host assemblies. That matches the proven compatibility pattern used by the working Bandcamp/Deezer plugin builds on Lidarr 3.x plugin hosts.
