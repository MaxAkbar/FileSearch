# Public Outlook regression fixtures

`mail-samples.zip` contains public upstream test files, without modifications. Tests unpack the archive into an isolated temporary directory. No personal mailbox was captured. The archive is test-only and is not included in product publishing.

| Archived file | Upstream source | SHA-256 |
| --- | --- | --- |
| `bodies.pst` | [Apache Tika testPST_variousBodyTypes.pst](https://github.com/apache/tika/blob/main/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-microsoft-module/src/test/resources/test-documents/testPST_variousBodyTypes.pst) | `24C5E6BBB8BF26A817C977283E40E7B69D2661FEC0845ABBE177F97EFCB05FB0` |
| `message.msg` | [Apache Tika testMSG.msg](https://github.com/apache/tika/blob/main/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-microsoft-module/src/test/resources/test-documents/testMSG.msg) | `972901D1A0049ED54DC993BA57F6612DB4C370ACEA818601B2133423885070CF` |
| `sample.ost` | [Aspose.Email SampleOstFile.ost](https://github.com/aspose-email/Aspose.Email-for-.NET/blob/master/Examples/Data/MAPI/SampleOstFile.ost) | `5FF8BC133935A03FAD8241F271A604E1CC41E36640D6538D6BCC9BD39E6D9866` |

The Tika files are distributed under Apache-2.0; see [LICENSE-Apache.txt](LICENSE-Apache.txt) and [NOTICE-Apache.txt](NOTICE-Apache.txt). The Aspose example file is distributed under its MIT license; see [LICENSE-Aspose.txt](LICENSE-Aspose.txt). Source links describe provenance; hashes identify the exact snapshots retained here.

The PST has four messages spanning plain, HTML, and RTF body representations, including duplicate subjects. The OST has 92 items. Generated MSG test files cover Unicode/ANSI properties, attachment names without payload streams, HTML script removal, and RTF-only bodies. Reader tests verify distinct identities, preview line numbers, live/indexed parity, stale-store rejection, output limits, and file-handle release.
