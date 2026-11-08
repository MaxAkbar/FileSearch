# Third-party dependencies

These notices cover the libraries added for local Outlook mail search and built-in file previews. They supplement the licenses of the application's other dependencies.

| Package | Version | Copyright | License | Source |
| --- | --- | --- | --- | --- |
| OpenMcdf | 3.4.0 | 2010-2026 Federico Blaseotto, Jeremy Powell | [MPL-2.0](docs/licenses/OpenMcdf-MPL-2.0.txt) | [Exact source revision](https://github.com/openmcdf/openmcdf/tree/687fae7286167035a2a1f2c7ab39720679a187a5) |
| XstReader.Api | 1.0.6 | 2016 Dijji; 2021 iluvadev | [MS-PL](docs/licenses/XstReader-MS-PL.txt) | [Source repository](https://github.com/iluvadev/XstReader) |
| RtfPipe | 2.0.7677.4303 | 2018 Eric Domke | [MIT](docs/licenses/RtfPipe-MIT.txt) | [Source repository](https://github.com/erdomke/RtfPipe) |
| System.Security.Cryptography.Pkcs | 10.0.14 | .NET Foundation and Contributors | [MIT](docs/licenses/DotNet-MIT.txt) | [Source repository](https://github.com/dotnet/runtime) |

OpenMcdf, XstReader.Api, and RtfPipe are used as unmodified compiled libraries. OpenMcdf's corresponding source is available at the revision linked above. FileSearch's store-disposal adapter is application code and does not patch the XstReader assembly. The explicit Pkcs reference upgrades XstReader's older transitive dependency.

Product build and publish outputs include this notice and the license texts in `docs/licenses`. Test fixture provenance and separate upstream licenses are recorded in [the fixture README](tests/FileSearch.Core.Tests/Fixtures/Outlook/README.md); fixtures are not product content.

## Built-in file preview

SQLite database search additionally uses unmodified Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core 10.0.14 ([MIT](docs/licenses/DotNet-MIT.txt), [source](https://github.com/dotnet/efcore)) and SQLitePCLRaw 2.1.13 (core, bundle, provider, and native library packages; Copyright SourceGear, LLC; [Apache-2.0](docs/licenses/SQLitePCLRaw-Apache-2.0.txt), [source](https://github.com/ericsink/SQLitePCL.raw)). The bundled native SQLite engine is [public domain](https://www.sqlite.org/copyright.html). CSharpDB.Engine 4.7.0 and its matching CSharpDB dependencies are unmodified libraries, Copyright 2026 CSharpDB Contributors ([MIT](docs/licenses/CSharpDB-MIT.txt), [source revision](https://github.com/MaxAkbar/CSharpDB/tree/a8e21be4e7ac464b12f20017fd4d249c4c33ec24)). Database reads use private temporary copies and do not redistribute or require a separate database application.

| Package | Version | Copyright | License | Source |
| --- | --- | --- | --- | --- |
| AvalonEdit | 6.3.1.120 | AvalonEdit Contributors; 2000-2025 AlphaSierraPapa for the SharpDevelop Team | [MIT](docs/licenses/AvalonEdit-MIT.txt) | [Exact source revision](https://github.com/icsharpcode/AvalonEdit/tree/862415d51eddc9eac93f462dbc522ffbf929cd52) |
| Markdig | 1.6.0 | 2016-2026 Alexandre Mutel | [BSD-2-Clause](docs/licenses/Markdig-BSD-2-Clause.txt) | [Exact source revision](https://github.com/xoofx/markdig/tree/8b4ffd59be3c5d7e80b5c097ec384e7c583ceb0f) |
| MimeKit | 4.18.2 | 2012-2026 .NET Foundation and Contributors | [MIT](docs/licenses/MimeKit-MIT.txt) | [Source repository](https://github.com/jstedfast/MimeKit) |
| HtmlSanitizer | 11.0.16 | Michael Ganss and HtmlSanitizer contributors | [MIT](docs/licenses/HtmlSanitizer-MIT.txt) | [Exact source revision](https://github.com/mganss/HtmlSanitizer/tree/87869b6e6d60cb9da1897e69f2a5190686adc785) |
| AngleSharp | 1.9.1 | 2013-2026 AngleSharp | [MIT](docs/licenses/AngleSharp-MIT.txt) | [Exact source revision](https://github.com/AngleSharp/AngleSharp/tree/3dd5498c13c49eceaaa5b607d0f0001c0f0499d8) |
| AngleSharp.Css | 1.3.2 | 2013-2026 AngleSharp | [MIT](docs/licenses/AngleSharp.Css-MIT.txt) | [Exact source revision](https://github.com/AngleSharp/AngleSharp.Css/tree/6c295c718d9030c1c64c91d626ee7631f4c30f96) |
| BouncyCastle.Cryptography | 2.7.0 | 2000-2026 The Legion of the Bouncy Castle Inc. | [MIT](docs/licenses/BouncyCastle-License.txt) | [Source repository](https://github.com/bcgit/bc-csharp) |

These libraries are used unmodified for read-only syntax highlighting, Markdown, MIME mail parsing, and HTML/CSS sanitization. BouncyCastle is MimeKit's unmodified transitive dependency. Spreadsheet and book package readers use .NET XML/ZIP APIs; Windows codecs provide WebP/GIF/TIFF frames. Preview handlers supplied by Windows or other installed applications remain external components; FileSearch does not redistribute them or require PowerToys.
