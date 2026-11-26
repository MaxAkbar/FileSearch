# Third-party dependencies

These notices cover the libraries added for local Outlook mail search and built-in file previews. They supplement the licenses of the application's other dependencies.

| Package | Version | Copyright | License | Source |
| --- | --- | --- | --- | --- |
| OpenMcdf | 3.4.0 | 2010-2026 Federico Blaseotto, Jeremy Powell | [MPL-2.0](docs/licenses/OpenMcdf-MPL-2.0.txt) | [Exact source revision](https://github.com/openmcdf/openmcdf/tree/687fae7286167035a2a1f2c7ab39720679a187a5) |
| XstReader.Api | 1.0.6 | 2016 Dijji; 2021 iluvadev | [MS-PL](docs/licenses/XstReader-MS-PL.txt) | [Source repository](https://github.com/iluvadev/XstReader) |
| RtfPipe | 2.0.7677.4303 | 2018 Eric Domke | [MIT](docs/licenses/RtfPipe-MIT.txt) | [Source repository](https://github.com/erdomke/RtfPipe) |
| System.Security.Cryptography.Pkcs | 11.0.0 | .NET Foundation and Contributors | [MIT](docs/licenses/DotNet-MIT.txt) | [Source repository](https://github.com/dotnet/runtime) |

OpenMcdf, XstReader.Api, and RtfPipe are used as unmodified compiled libraries. OpenMcdf's corresponding source is available at the revision linked above. FileSearch's store-disposal adapter is application code and does not patch the XstReader assembly. The explicit Pkcs reference upgrades XstReader's older transitive dependency.

Product build and publish outputs include this notice and the license texts in `docs/licenses`. Test fixture provenance and separate upstream licenses are recorded in [the fixture README](tests/FileSearch.Core.Tests/Fixtures/Outlook/README.md); fixtures are not product content.

## Built-in file preview

SQLite database search additionally uses unmodified Microsoft.Data.Sqlite and Microsoft.Data.Sqlite.Core 11.0.0 ([MIT](docs/licenses/DotNet-MIT.txt), [source](https://github.com/dotnet/efcore)) and SQLitePCLRaw 3.0.5 (core, bundle, configuration, and provider packages; Copyright SourceGear, LLC; [Apache-2.0](docs/licenses/SQLitePCLRaw-Apache-2.0.txt), [source](https://github.com/ericsink/SQLitePCL.raw)). The bundled SQLite 3.53.4 native engine is [public domain](https://www.sqlite.org/copyright.html). CSharpDB.Engine 5.0.0 and its matching CSharpDB dependencies are unmodified libraries, Copyright 2026 CSharpDB Contributors ([MIT](docs/licenses/CSharpDB-MIT.txt), [source revision](https://github.com/MaxAkbar/CSharpDB/tree/7af7b482d4298a00fe2d1b6aaac6e09b806fb778)). Database reads use private temporary copies and do not redistribute or require a separate database application.

| Package | Version | Copyright | License | Source |
| --- | --- | --- | --- | --- |
| AvalonEdit | 6.3.1.120 | AvalonEdit Contributors; 2000-2025 AlphaSierraPapa for the SharpDevelop Team | [MIT](docs/licenses/AvalonEdit-MIT.txt) | [Exact source revision](https://github.com/icsharpcode/AvalonEdit/tree/862415d51eddc9eac93f462dbc522ffbf929cd52) |
| Markdig | 1.6.0 | 2016-2026 Alexandre Mutel | [BSD-2-Clause](docs/licenses/Markdig-BSD-2-Clause.txt) | [Exact source revision](https://github.com/xoofx/markdig/tree/8b4ffd59be3c5d7e80b5c097ec384e7c583ceb0f) |
| MimeKit, MimeKit.Core, MimeKit.Cryptography | 5.0.0 | 2012-2026 .NET Foundation and Contributors | [MIT](docs/licenses/MimeKit-MIT.txt) | [Source repository](https://github.com/jstedfast/MimeKit) |
| HtmlSanitizer | 11.0.26 | Michael Ganss and HtmlSanitizer contributors | [MIT](docs/licenses/HtmlSanitizer-MIT.txt) | [Exact source revision](https://github.com/mganss/HtmlSanitizer/tree/eec723e5b6f4372b5ed00c44b384f500e39be50b) |
| AngleSharp | 1.10.0 | 2013-2026 AngleSharp | [MIT](docs/licenses/AngleSharp-MIT.txt) | [Exact source revision](https://github.com/AngleSharp/AngleSharp/tree/49d47510cbcdc91c1327527f7df9228749059476) |
| AngleSharp.Css | 1.4.0 | 2013-2026 AngleSharp | [MIT](docs/licenses/AngleSharp.Css-MIT.txt) | [Exact source revision](https://github.com/AngleSharp/AngleSharp.Css/tree/029884ef5eaa26104552a45c5f4292e848c086d4) |
| BouncyCastle.Cryptography | 2.7.0 | 2000-2026 The Legion of the Bouncy Castle Inc. | [MIT](docs/licenses/BouncyCastle-License.txt) | [Source repository](https://github.com/bcgit/bc-csharp) |

These libraries are used unmodified for read-only syntax highlighting, Markdown, MIME mail parsing, and HTML/CSS sanitization. BouncyCastle is MimeKit's unmodified transitive dependency. Spreadsheet and book package readers use .NET XML/ZIP APIs; Windows codecs provide WebP/GIF/TIFF frames. Preview handlers supplied by Windows or other installed applications remain external components; FileSearch does not redistribute them or require PowerToys.
