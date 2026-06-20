using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using FileSearch.Core.Engine;
using FileSearch.Core.Indexing;
using FileSearch.Gui.Settings;
using FileSearch.Gui.ViewModels;

namespace FileSearch.Gui.Tests;

/// <summary>
/// WPF resolves binding paths at runtime and fails them silently — a typo'd
/// path just deadens a control. This audit discovers every XAML file in the
/// GUI project, extracts every binding path with its XML scope, and resolves
/// it by reflection against the types that can actually be its DataContext:
/// paths at window scope must resolve on the window's view model, paths
/// inside a DataTemplate must resolve on an item type (so a shell-prefixed
/// path accidentally written into an item template fails the audit), and
/// paths inside styles/control templates/menus may resolve on either.
/// </summary>
public sealed class BindingPathAuditTests
{
    /// <summary>Types item templates bind against.</summary>
    private static readonly Type[] s_itemTypes =
    {
        typeof(FileResultViewModel),
        typeof(Hit),
        typeof(IndexValidationDriftInfo),
        typeof(IndexRootHealthInfo),
        typeof(IndexedLocationSettings),
        typeof(IndexFilterListSettings),
        typeof(FavoriteResultSettings),
        typeof(WorkspaceSettings),
        typeof(RegexTestGroupViewModel),
        typeof(RegexTestMatchViewModel),
        typeof(SavedSearchSettings),
        typeof(SidebarScopeItem),
        typeof(SearchScope),
        typeof(WorkflowLibraryItemViewModel),
        typeof(WorkflowStepViewModel),
        typeof(SearchStepViewModel),
        typeof(IfStepViewModel),
        typeof(RetryStepViewModel),
        typeof(ForEachStepViewModel),
        typeof(ExportStepViewModel),
        typeof(FileOperationStepViewModel),
        typeof(RunProgramStepViewModel),
        typeof(StopStepViewModel),
        typeof(WorkflowConditionViewModel),
        typeof(WorkflowParameterSetViewModel),
        typeof(QuickIndexedLocationSelection),
        typeof(AppShortcutBindingViewModel),
        typeof(QuickSearchShortcutBindingViewModel),
        typeof(QueryChipViewModel),
    };

    /// <summary>Window-scope (DataContext) type per file; default is the shell.</summary>
    private static readonly Dictionary<string, Type> s_rootTypeOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AboutWindow.xaml"] = typeof(AboutWindow),
        ["RegexTesterWindow.xaml"] = typeof(RegexTesterViewModel),
        ["SettingsWindow.xaml"] = typeof(ApplicationSettingsViewModel),
        ["QuickSearchWindow.xaml"] = typeof(QuickSearchViewModel),
        ["ImageOcrPreviewWindow.xaml"] = typeof(ImageOcrPreviewViewModel),
    };

    // Matches a {Binding ...} expression with up to two nesting levels of
    // inner markup extensions (Converter={...}, AncestorType={x:Type ...}).
    private static readonly Regex s_bindingExpression = new(
        @"\{Binding(?<body>(?:[^{}]|\{(?:[^{}]|\{[^{}]*\})*\})*)\}",
        RegexOptions.Compiled);

    private static readonly Regex s_positionalPath = new(
        @"^\s+(?:Path=)?(?<path>[A-Za-z_][A-Za-z0-9_.]*)(?=\s|,|$)",
        RegexOptions.Compiled);

    private static readonly Regex s_namedPath = new(
        @"[\s,]Path=(?<path>[A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled);

    private sealed record BindingSite(string File, string Expression, string Path, Scope Scope);

    private enum Scope
    {
        /// <summary>Window-level: must resolve on the window's view model.</summary>
        Root,

        /// <summary>Inside a DataTemplate: must resolve on an item type.</summary>
        ItemTemplate,

        /// <summary>Styles/control templates/menus: applied in varying contexts.</summary>
        Flexible,
    }

    [Fact]
    public void EveryBindingPathResolvesAgainstItsDataContextType()
    {
        var root = FindRepositoryRoot();
        var xamlFiles = Directory.GetFiles(Path.Combine(root, "src", "FileSearch.Gui"), "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(xamlFiles);

        var unresolved = new List<string>();
        foreach (var file in xamlFiles)
        {
            var fileName = Path.GetFileName(file);
            var rootType = s_rootTypeOverrides.TryGetValue(fileName, out var t) ? t : typeof(MainViewModel);

            foreach (var site in ExtractBindingSites(file))
            {
                if (!Resolves(site, rootType))
                    unresolved.Add($"{fileName} [{site.Scope}]: {site.Path}");
            }
        }

        Assert.True(
            unresolved.Count == 0,
            "Binding paths that don't resolve on their DataContext type (typo or missed/extra prefix?):\n" +
            string.Join('\n', unresolved));
    }

    private static bool Resolves(BindingSite site, Type rootType)
    {
        var path = site.Path;

        // Relative-source plumbing into the context menu's host, not a VM path.
        if (path.StartsWith("PlacementTarget.", StringComparison.Ordinal))
            return true;

        // Bindings that target a UI element rather than the DataContext.
        if (site.Expression.Contains("ElementName=", StringComparison.Ordinal))
            return true;

        if (site.Expression.Contains("RelativeSource", StringComparison.Ordinal))
        {
            // The explicit escape from an item template to an ancestor's
            // DataContext — the window's view model, or an intermediate
            // template's item (e.g. a per-item button bound to a command on
            // the list-owning step view model).
            if (path.StartsWith("DataContext.", StringComparison.Ordinal))
            {
                var rest = path["DataContext.".Length..];
                return ResolvesOn(rootType, rest) || s_itemTypes.Any(type => ResolvesOn(type, rest));
            }

            // Self/TemplatedParent/Ancestor control-property bindings.
            return true;
        }

        return site.Scope switch
        {
            Scope.Root => ResolvesOn(rootType, path),
            Scope.ItemTemplate => s_itemTypes.Any(type => ResolvesOn(type, path)),
            _ => ResolvesOn(rootType, path) || s_itemTypes.Any(type => ResolvesOn(type, path)),
        };
    }

    private static bool ResolvesOn(Type type, string path)
    {
        var current = type;
        foreach (var segment in path.Split('.'))
        {
            var property = current.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
                return false;

            current = property.PropertyType;
        }

        return true;
    }

    private static IEnumerable<BindingSite> ExtractBindingSites(string file)
    {
        var sites = new List<BindingSite>();
        var dataTemplateDepth = 0;
        var flexibleDepth = 0;
        var openElements = new Stack<ElementKind>();

        using var reader = XmlReader.Create(file);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var isEmpty = reader.IsEmptyElement;
                var classification = Classify(reader.LocalName);

                // An explicit DataContext attribute rebases the element and
                // its subtree to an unknown type — audit those permissively
                // (the rebase expression itself is audited at the outer scope).
                var hasRebase = reader.GetAttribute("DataContext") is not null;
                if (hasRebase && classification == ElementKind.Plain)
                    classification = ElementKind.Flexible;

                var outerScope = CurrentScope(dataTemplateDepth, flexibleDepth, classification);

                // <Binding Path="..."/> element form (MultiBinding children).
                if (reader.LocalName == "Binding")
                {
                    var pathAttribute = reader.GetAttribute("Path");
                    if (pathAttribute is not null && Regex.IsMatch(pathAttribute, "^[A-Za-z_][A-Za-z0-9_.]*$"))
                        sites.Add(new BindingSite(file, reader.GetAttribute("ElementName") is null ? "" : "ElementName=", pathAttribute, outerScope));
                }

                if (reader.HasAttributes)
                {
                    for (var i = 0; i < reader.AttributeCount; i++)
                    {
                        reader.MoveToAttribute(i);
                        var attributeScope = hasRebase && reader.LocalName != "DataContext"
                            ? Scope.Flexible
                            : outerScope;

                        foreach (Match expression in s_bindingExpression.Matches(reader.Value))
                        {
                            var body = expression.Groups["body"].Value;
                            var path = s_positionalPath.Match(body) is { Success: true } positional
                                ? positional.Groups["path"].Value
                                : s_namedPath.Match(body) is { Success: true } named
                                    ? named.Groups["path"].Value
                                    : null;

                            if (path is not null)
                                sites.Add(new BindingSite(file, expression.Value, path, attributeScope));
                        }
                    }

                    reader.MoveToElement();
                }

                if (!isEmpty)
                {
                    openElements.Push(classification);
                    if (classification == ElementKind.DataTemplate) dataTemplateDepth++;
                    else if (classification == ElementKind.Flexible) flexibleDepth++;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                var classification = openElements.Pop();
                if (classification == ElementKind.DataTemplate) dataTemplateDepth--;
                else if (classification == ElementKind.Flexible) flexibleDepth--;
            }
        }

        return sites;
    }

    private enum ElementKind
    {
        Plain,
        DataTemplate,
        Flexible,
    }

    private static ElementKind Classify(string localName) => localName switch
    {
        "DataTemplate" or "HierarchicalDataTemplate" => ElementKind.DataTemplate,
        "Style" or "ControlTemplate" or "ItemsPanelTemplate" or "ContextMenu" or "ToolTip" => ElementKind.Flexible,
        _ => ElementKind.Plain,
    };

    private static Scope CurrentScope(int dataTemplateDepth, int flexibleDepth, ElementKind entering)
    {
        // Attributes on the template/style element itself still belong to the
        // outer scope; only content nested inside changes context.
        if (dataTemplateDepth > 0)
            return Scope.ItemTemplate;
        if (flexibleDepth > 0 || entering == ElementKind.Flexible || entering == ElementKind.DataTemplate)
            return Scope.Flexible;
        return Scope.Root;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FileSearch.slnx")))
                return directory.FullName;
            directory = directory.Parent!;
        }

        throw new InvalidOperationException("Repository root (FileSearch.slnx) not found above test directory.");
    }
}
