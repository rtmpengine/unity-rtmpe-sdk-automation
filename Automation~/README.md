# RTMPE automation kit

The headless engine behind **Window → RTMPE → Conversion Wizard** and
**Window → RTMPE → Network Readiness**. It also travels inside the Unity package,
under `Automation~/`, where the asset pipeline leaves it alone — this archive is
the same engine for anyone who wants it without a Unity project around it.

## Licence

This kit is part of the RTMPE SDK and is governed by `LICENSE.md` beside this
file — a limited, service-linked licence, not an open-source one. Using it
requires an RTMPE account in good standing. It may not be used to implement,
host or operate a server that speaks the RTMPE wire protocol, and it may not be
redistributed separately from an application of yours.

## What you need

A .NET 8 SDK on `PATH`. Check with `dotnet --list-sdks` — one line must start
with `8.`. Do NOT check with `dotnet --version`: it reports the SDK selected in
the current directory, so a machine carrying both 8 and 9 prints `9.x` and looks
wrong while being correct.

## Build it once

```bash
cd clients/unity-sdk
dotnet build Tooling/RTMPE.SDK.ConversionCli -c Release --nologo
```

⚠️ From `clients/unity-sdk`, not from inside `Tooling/`. That folder carries a
`global.json` pinning one exact SDK for the repository's byte-compared analyzer
builds; building from within it demands that exact patch level and fails on any
other. Everything here passes absolute paths, so the working directory is the
only thing that decision depends on.

## Score your Unity project

Write the artifact into the project root — the folder holding `Assets/` — which
is where both windows read it:

```bash
dotnet clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli/bin/Release/net8.0/RTMPE.SDK.ConversionCli.dll \
  readiness --source "/path/to/YourUnityProject" --out "/path/to/YourUnityProject"
```

Then in Unity: **Window → RTMPE → Network Readiness** → **Refresh**.

## Point the wizard here

**Window → RTMPE → Conversion Wizard** → **Browse…** → select

```
<this kit>/clients/unity-sdk/Tooling/RTMPE.SDK.ConversionCli
```

That is the CLI **project folder**, not this kit's root.

## The one measurement we need back

The shipped analyzer is pinned against Roslyn `4.3.0`, and whether that is at or
below your editor's own compiler is the one fact this project cannot establish
without a Unity install. Run this on the machine with the **oldest** Unity you
intend to support:

```bash
bash scripts/check-roslyn-version.sh
```

It finds `csc.dll`, prints `4.3.0 <= host? YES/NO`, and appends a dated verdict
to `clients/unity-sdk/Tooling/COMPILER_COMPATIBILITY.md` inside this kit. Send
that file back — it names the `csc.dll` it read, so the verdict is self-evident.

## Everything else

The full guide ships inside the Unity package, at
`Packages/com.rtmpe.sdk/Documentation~/automation.md`, and its **Documentation**
button is in both windows.
