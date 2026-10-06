# Contributing

Issues and pull requests are welcome. This is a small project maintained in spare time, so replies
can take a while.

- **Build and test** with the .NET SDK (8 or newer): `dotnet test tests/HeadsetAutoSwitch.Tests.csproj`
  and `dotnet build src/HeadsetAutoSwitch.csproj -c Release`. The tests also run on macOS and Linux.
- **Target:** the app targets .NET Framework 4.8 (built into Windows) with current C#. APIs that only
  exist in modern .NET won't compile. Warnings are errors.
- **Style:** follow the surrounding code (see `.editorconfig`); keep the app a single small `.exe`
  without NuGet packages.
- **New headsets:** the easiest route is adding support to
  [HeadsetControl](https://github.com/Sapd/HeadsetControl), which this app already uses. Built-in
  support makes sense when a headset reports on/off by itself (no polling), like the Cloud Alpha 2.
