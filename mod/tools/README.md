# Build tools — .NET Framework 4.8 reference assemblies

This folder lets the mod build **without** installing the .NET 4.8 Developer Pack.
The `PlagueDash.csproj` points MSBuild at it via `<FrameworkPathOverride>`.

## What's here

`net48ref/build/.NETFramework/v4.8/` — the full Framework 4.8 reference assemblies
(237 DLLs), extracted from the
[`Microsoft.NETFramework.ReferenceAssemblies.net48`](https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies.net48)
NuGet package (v1.0.3). These are the same files the Developer Pack installs.

## How they were obtained (reproduce if deleted)

```bash
cd tools
curl -o net48refs.nupkg "https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net48/1.0.3/microsoft.netframework.referenceassemblies.net48.1.0.3.nupkg"
mkdir -p net48ref && cd net48ref
python -c "import zipfile; z=zipfile.ZipFile('../net48refs.nupkg'); z.extractall('.', [n for n in z.namelist() if n.startswith('build/.NETFramework/v4.8/')])"
rm ../net48refs.nupkg
```

## When you don't need this

If you have the **.NET Framework 4.8 Developer Pack** (or full Visual Studio with
the .NET Framework targeting pack) installed, MSBuild finds the reference
assemblies automatically and ignores `<FrameworkPathOverride>`. You can delete
this folder in that case — it's only a fallback for minimal Build Tools installs.
