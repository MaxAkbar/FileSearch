using System.Globalization;

namespace FileSearch.Benchmarks;

/// <summary>Declared before NW-08b scorer changes. Do not tune the held-out partition.</summary>
internal static class QuickNameTaskCorpus
{
    public const string Version = "quick-name-tasks-v1";

    public static QuickNameDefinition Create(int distractors)
    {
        var fixtures = new List<QuickNameFixture>();
        void Repo(string path) => fixtures.Add(new("repository/" + path, "Repository filename only: " + path));
        foreach (string path in new[]
        {
            "src/FileSearch.Gui/ViewModels/QuickSearchViewModel.cs",
            "src/FileSearch.Gui/ViewModels/FileResultViewModel.cs",
            "src/FileSearch.Core/Databases/DatabaseReadSession.Paging.cs",
            "src/FileSearch.Gui/Services/SearchLocationResolver.cs",
            "src/FileSearch.Gui/Services/PdfPreviewText.cs",
            "src/FileSearch.Core/Queries/QueryFactory.cs",
            "src/FileSearch.Core/Queries/IQueryFactory.cs",
            "src/FileSearch.Core/Volumes/VolumeTermSearch.cs",
            "src/FileSearch.Gui/Controls/DatabasePreviewControl.Find.cs",
            "src/FileSearch.Gui/Settings/AppSettings.cs",
            "tests/FileSearch.Core.Tests/QueryFactoryTests.cs",
            "tests/FileSearch.Gui.Tests/QuickSearchScopeHandoffTests.cs",
            "README.Mail.md", "README.md", ".github/workflows/release.yml",
        }) Repo(path);

        string deepReport = "common/Deep/" + string.Join('/', Enumerable.Repeat("d", 40)) + "/report.docx";
        foreach (string path in new[]
        {
            "common/Engineering/notes-git-reference.md", "common/Engineering/digital.docx",
            "common/Animals/cat-care-notes.txt", "common/Animals/catalogue.txt",
            "common/Code/CacheEntryPolicy.cs", "common/Code/CacheEntryish.cs",
            "common/Code/XMLReaderOptions.cs", "common/Code/xmlreader-cache.cs",
            "common/Code/TaskQueueManager.cs", "common/Code/taskqueue.cs",
            "common/Code/PaymentStatusView.cs", "common/Code/PaymentStatusish.cs",
            "common/Projects/Orion/project plan.md", "common/Projects/Lyra/project plan.md",
            "common/Finance/AnnualReview_Q4_2026.xlsx", "common/Finance/Q4_2026_Archive_2019.xlsx",
            "common/Guides/C++ language guide.md", "common/Guides/C++notes.md",
            "common/Reports/report-draft.docx", deepReport,
            "common/Vcs/digital-git-guide.md", "common/Vcs/digital-history.md",
            "common/Diagnostics/system-log-overview.txt", "common/Diagnostics/catalogue-records.txt",
            "common/Cv/Résumé_2026_Final.pdf", "common/Cv/Résumé_2026Finalish.pdf",
            "common/Literal/a[b].txt", "common/Literal/a[b]-notes.txt", "common/Literal/OR.txt",
            "common/Duplicates/ProjectA/README.md", "common/Duplicates/ProjectB/README.md",
            "common/Backups/QuickSearchViewModel.cs.bak", "common/Backups/README.Mail.backup.md",
            "common/Backups/QueryFactoryTests.cs.bak", "common/Backups/release.yml.notes",
            "common/Code/previewtextcache.cs",
        }) fixtures.Add(new(path, "Generated standard source/document naming pattern; empty content."));

        var tasks = new List<QuickNameTask>();
        void Task(string id, string partition, string query, string? target, string description, bool noResults = false, params string[] roots) =>
            tasks.Add(new(id, partition, query, target, noResults, roots, description));
        const string calibration = "calibration";
        const string heldOut = "held-out";
        Task("c01", calibration, "QuickSearchViewModel.cs", "repository/src/FileSearch.Gui/ViewModels/QuickSearchViewModel.cs", "Exact repository filename versus backup suffix.");
        Task("c02", calibration, "file result", "repository/src/FileSearch.Gui/ViewModels/FileResultViewModel.cs", "Multiple literal terms inside a PascalCase repository filename.");
        Task("c03", calibration, "git", "common/Engineering/notes-git-reference.md", "Complete delimiter token versus a shorter embedded substring.", roots: ["common/Engineering"]);
        Task("c04", calibration, "cat", "common/Animals/cat-care-notes.txt", "Complete prefix token versus a longer-word prefix.", roots: ["common/Animals"]);
        Task("c05", calibration, "cache entry", "common/Code/CacheEntryPolicy.cs", "PascalCase complete terms versus an embedded longer term.");
        Task("c06", calibration, "DatabaseReadSession.Paging", "repository/src/FileSearch.Core/Databases/DatabaseReadSession.Paging.cs", "Dotted repository stem remains literal.");
        Task("c07", calibration, "README.Mail", "repository/README.Mail.md", "Exact dotted stem versus backup filename.");
        Task("c08", calibration, "project plan", "common/Projects/Orion/project plan.md", "Duplicate leaf names restricted to an explicit folder.", roots: ["common/Projects/Orion"]);
        Task("c09", calibration, "Q4 2026", "common/Finance/AnnualReview_Q4_2026.xlsx", "Literal numeric terms in a standard finance document name.");
        Task("c10", calibration, "release.yml", "repository/.github/workflows/release.yml", "Exact public workflow filename with punctuation.");
        Task("c11", calibration, "C++", "common/Guides/C++ language guide.md", "Punctuation-bearing language token versus adjoining letters.", roots: ["common/Guides"]);
        Task("c12", calibration, "report", deepReport, "Protected deep exact stem versus a shallow prefix.", roots: ["common/Deep", "common/Reports"]);
        Task("c13", calibration, "zz-no-file-marker-457", null, "Literal no-match control.", true);
        Task("c14", calibration, "qsvm", "repository/src/FileSearch.Gui/ViewModels/QuickSearchViewModel.cs", "Strict acronym control; no automatic expansion.", true);
        Task("c15", calibration, "DatabsaeReadSession", "repository/src/FileSearch.Core/Databases/DatabaseReadSession.Paging.cs", "Strict typo control; no automatic correction.", true);
        Task("c16", calibration, "report.*", null, "Regex-looking punctuation remains literal.", true);

        Task("h01", heldOut, "QueryFactoryTests.cs", "repository/tests/FileSearch.Core.Tests/QueryFactoryTests.cs", "Held-out exact repository filename versus backup.");
        Task("h02", heldOut, "preview text", "repository/src/FileSearch.Gui/Services/PdfPreviewText.cs", "Held-out PascalCase repository words versus lowercase compound.");
        Task("h03", heldOut, "reader", "common/Code/XMLReaderOptions.cs", "Acronym-to-Pascal boundary versus an embedded lowercase substring.");
        Task("h04", heldOut, "queue", "common/Code/TaskQueueManager.cs", "Lowercase-to-uppercase boundary versus lowercase compound.");
        Task("h05", heldOut, "git", "common/Vcs/digital-git-guide.md", "A later complete occurrence follows an earlier embedded occurrence.", roots: ["common/Vcs"]);
        Task("h06", heldOut, "log", "common/Diagnostics/system-log-overview.txt", "Held-out delimiter token versus embedded longer word.", roots: ["common/Diagnostics"]);
        Task("h07", heldOut, "payment status", "common/Code/PaymentStatusView.cs", "Held-out multiple PascalCase terms with complete boundaries.");
        Task("h08", heldOut, "réSUMé 2026", "common/Cv/Résumé_2026_Final.pdf", "Unicode/case and numeric literal terms.");
        Task("h09", heldOut, "a[b]", "common/Literal/a[b].txt", "Brackets are literal; exact stem remains protected.");
        Task("h10", heldOut, "README", "common/Duplicates/ProjectB/README.md", "Held-out duplicate leaf name inside an explicit selected root.", roots: ["common/Duplicates/ProjectB"]);
        Task("h11", heldOut, "OR", "common/Literal/OR.txt", "Operator word is literal with a protected exact stem.");
        Task("h12", heldOut, @"Projects\Lyra", "common/Projects/Lyra", "Literal path separator and folder target.");
        Task("h13", heldOut, "PaymnetStatus", "common/Code/PaymentStatusView.cs", "Held-out strict typo control.", true);
        Task("h14", heldOut, "prvtxt", "repository/src/FileSearch.Gui/Services/PdfPreviewText.cs", "Held-out strict acronym control.", true);
        Task("h15", heldOut, "\"project plan\"", null, "Quote characters remain literal rather than phrase syntax.", true);
        Task("h16", heldOut, "git OR queue", null, "Literal words do not enable Boolean OR.", true);

        return new(Version, "name-distractors-v1", distractors, 80,
            ["QuarterlyReportArchive-{0:D6}.txt", "digital-release-notes-{0:D6}.md", "catalogue-record-{0:D6}.txt",
             "cacheentryish-{0:D6}.cs", "taskqueuemanager-cache-{0:D6}.cs", "xmlreader-cache-{0:D6}.cs",
             "paymentstatusish-{0:D6}.cs", "previewtextcache-{0:D6}.cs", "project plan archived {0:D6}.md",
             "report backup {0:D6}.docx", "ordinary-reference-{0:D6}.txt", "benchmark-unrelated-{0:D6}.bin"],
            fixtures.ToArray(), ["common/Projects/Lyra"], tasks.ToArray());
    }

    public static IEnumerable<string> FileNames(QuickNameDefinition definition)
    {
        foreach (var fixture in definition.Fixtures) yield return fixture.RelativePath;
        for (int index = 0; index < definition.DistractorCount; index++)
            yield return "synthetic/shard-" + (index / 1000).ToString("D3", CultureInfo.InvariantCulture) + "/" +
                string.Format(CultureInfo.InvariantCulture, definition.DistractorTemplates[index % definition.DistractorTemplates.Length], index);
    }
}

internal sealed record QuickNameFixture(string RelativePath, string Provenance);
internal sealed record QuickNameTask(string Id, string Partition, string Query, string? TargetRelativePath, bool ExpectNoResults, string[] ScopeRoots, string Description);
internal sealed record QuickNameDefinition(string Version, string GeneratorVersion, int DistractorCount, int MaxResults, string[] DistractorTemplates, QuickNameFixture[] Fixtures, string[] Directories, QuickNameTask[] Tasks);
