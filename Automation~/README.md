# RTMPE automation kit

The conversion and scoring engine behind **Window → RTMPE → Conversion Wizard** and
**Window → RTMPE → Network Readiness**. These sources ship inside the Unity package under
`Automation~/`, and the same contents are published as the standalone archive
`rtmpe-automation-kit.tar.gz`, for use without a Unity project open.

## Licence

This kit is part of the RTMPE SDK and is governed by `LICENSE.md` next to this file — a
limited, service-linked licence, not an open-source one. Using it requires an RTMPE
account in good standing. It may not be used to implement, host or operate a server that
speaks the RTMPE wire protocol, and it may not be redistributed separately from an
application of yours.

## Requirements

- The .NET 8 SDK. `dotnet --list-sdks` must list a version starting with `8.`
  (`dotnet --version` reports the SDK selected for the current folder, which can be a
  newer one).
- Network access the first time the engine is built, to restore its NuGet packages.
- GNU `make`, only if you use the Makefile verbs.

## Build

From the kit's root folder:

```bash
dotnet build clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli -c Release --nologo
```

The Conversion Wizard and the Makefile verbs build the engine themselves; the step above
is needed only to run the built tool directly, as below.

## Score a Unity project

Write the readiness report into the Unity project's root folder (the folder that contains
`Assets/`), where the Unity windows read it:

```bash
dotnet clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli/bin/Release/net8.0/RTMPE.SDK.ConversionCli.dll \
  readiness --source "/path/to/YourUnityProject" --out "/path/to/YourUnityProject"
```

Then open **Window → RTMPE → Network Readiness** in Unity and press **Refresh**. The kit's
`Makefile` provides the same operations as verbs: `make readiness`, `make convert`,
`make fix` and `make gen-rpc`.

## Use the kit from the Conversion Wizard

The Conversion Wizard uses the engine inside the Unity package. When it cannot find one, it
shows a **Conversion host location** field: press **Browse…** there and select the CLI
project folder of an extracted archive:

```text
<kit>/clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli
```

## Check analyzer compatibility

The analyzers are built against Roslyn 4.3.0. To confirm that a Unity editor's compiler
can load them, run this on the machine with the oldest Unity version you support:

```bash
bash scripts/check-roslyn-version.sh
```

It locates the editor's `csc.dll` and prints whether its Roslyn version is 4.3.0 or later.

## Documentation

The full guide is `Documentation~/automation.md` in the Unity package; the
**Documentation** button in both windows opens it.
