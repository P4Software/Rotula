# Third-party notices

Rotula itself is MIT licensed (see [LICENSE](LICENSE)). It uses the components below. Versions and license expressions were
read from the project files and the license metadata in each package's `.nuspec`.

## Shipped with the application

| Component | Version | License | Used by | Project |
|---|---|---|---|---|
| SkiaSharp | 4.153.1 | MIT | Rotula.Rendering | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.Win32 (native Skia library, pulled in by SkiaSharp) | 4.153.1 | MIT | Rotula.Rendering | https://github.com/mono/SkiaSharp |
| ZXing.Net (barcode encoding only) | 0.16.11 | Apache-2.0 | Rotula.Rendering | https://github.com/micjahn/ZXing.Net |
| MiniExcel (reads `.xlsx`) | 1.46.0 | Apache-2.0 | Rotula.Core | https://github.com/mini-software/MiniExcel |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | Rotula.App | https://github.com/CommunityToolkit/dotnet |
| Liberation Sans Regular / Bold and Liberation Mono Regular (embedded stand-in fonts) | 2.x | SIL Open Font License 1.1 | Rotula.Rendering | https://github.com/liberationfonts/liberation-fonts |

The Liberation fonts are embedded unmodified in `Rotula.Rendering`; their license text is in
`src/Rotula.Rendering/Fonts/LICENSE-LiberationFonts.txt` and is also embedded in the assembly. Apache-2.0 components are
used unmodified; the Apache License 2.0 text is available at https://www.apache.org/licenses/LICENSE-2.0.

The self-contained release builds additionally include the .NET runtime and the Windows Desktop (WPF) runtime, which are MIT
licensed by Microsoft and the .NET Foundation (https://github.com/dotnet/runtime, https://github.com/dotnet/wpf).

## Build and test only (not shipped)

| Component | Version | License |
|---|---|---|
| xunit (and xunit.core / assert / extensibility packages) | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| Microsoft.CodeCoverage (via the test SDK) | 17.14.1 | MIT |
| Microsoft.TestPlatform.* (via the test SDK) | 17.14.1 | MIT |
| Newtonsoft.Json (via the test SDK) | 13.0.3 | MIT |
| coverlet.collector | 6.0.4 | MIT |
| dotnet-reportgenerator-globaltool (CI only, installed on demand) | latest | Apache-2.0 |
| Inno Setup (CI only, builds the installer) | 6.x | Inno Setup License (free, redistribution of the tool is not part of Rotula) |

## Copyright holders

- SkiaSharp: Copyright (c) Microsoft Corporation and contributors.
- ZXing.Net: Copyright ZXing.Net contributors (Michael Jahn and others).
- MiniExcel: Copyright Wei Lin, 2021 onwards.
- CommunityToolkit.Mvvm: Copyright (c) .NET Foundation and Contributors.
- Liberation fonts: Digitized data copyright (c) 2010 Google Corporation; copyright (c) 2012 Red Hat, Inc.
