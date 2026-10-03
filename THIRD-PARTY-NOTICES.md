# Mail search dependencies

These notices cover the libraries added for local Outlook mail search. They supplement the licenses of the application's other dependencies.

| Package | Version | Copyright | License | Source |
| --- | --- | --- | --- | --- |
| OpenMcdf | 3.3.0 | 2010-2026 Federico Blaseotto, Jeremy Powell | [MPL-2.0](docs/licenses/OpenMcdf-MPL-2.0.txt) | [Exact source revision](https://github.com/openmcdf/openmcdf/tree/11b5d876cdebb472f1845dfa55e9e9b953aed65f) |
| XstReader.Api | 1.0.6 | 2016 Dijji; 2021 iluvadev | [MS-PL](docs/licenses/XstReader-MS-PL.txt) | [Source repository](https://github.com/iluvadev/XstReader) |
| RtfPipe | 2.0.7677.4303 | 2018 Eric Domke | [MIT](docs/licenses/RtfPipe-MIT.txt) | [Source repository](https://github.com/erdomke/RtfPipe) |
| System.Security.Cryptography.Pkcs | 10.0.12 | .NET Foundation and Contributors | [MIT](docs/licenses/DotNet-MIT.txt) | [Source repository](https://github.com/dotnet/runtime) |

OpenMcdf, XstReader.Api, and RtfPipe are used as unmodified compiled libraries. OpenMcdf's corresponding source is available at the revision linked above. FileSearch's store-disposal adapter is application code and does not patch the XstReader assembly. The explicit Pkcs reference upgrades XstReader's older transitive dependency.

Product build and publish outputs include this notice and the license texts in `docs/licenses`. Test fixture provenance and separate upstream licenses are recorded in [the fixture README](tests/FileSearch.Core.Tests/Fixtures/Outlook/README.md); fixtures are not product content.
