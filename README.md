# Lidarr Plugin Release Reconciler

Release Reconciler is a Lidarr plugin that captures release mismatch evidence, evaluates candidate releases, and offers guarded operator-approved switching flows.

## Build

This repository uses the Lidarr source tree as a submodule under `ext/Lidarr`.

```bash
git submodule update --init --recursive
dotnet build Lidarr.Plugin.ReleaseReconciler.csproj -c Release /p:NuGetAudit=false /p:RunAnalyzers=false /p:RunAnalyzersDuringBuild=false /p:EnableNETAnalyzers=false /p:EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false /p:NoWarn=SA1200%3BNU1902
```

## Install

Copy the built plugin files into:

```
/mnt/user/appdata/lidarr/plugins/jtstothard/Lidarr.Plugin.ReleaseReconciler/
```

At minimum, deploy the release zip contents there and restart Lidarr.
