using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FileSearch.Gui.ViewModels;

public sealed partial class RegexTesterViewModel : ObservableObject
{
    private const int MaxMatches = 200;
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    [ObservableProperty] private string _pattern = string.Empty;
    [ObservableProperty] private string _testText = string.Empty;
    [ObservableProperty] private string _replacement = string.Empty;
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _multiline;
    [ObservableProperty] private bool _singleline;
    [ObservableProperty] private bool _isPatternValid = true;
    [ObservableProperty] private string _statusText = "Enter a pattern";
    [ObservableProperty] private string _replacementPreview = string.Empty;
    [ObservableProperty] private RegexTestMatchViewModel? _selectedMatch;

    public RegexTesterViewModel()
    {
    }

    public RegexTesterViewModel(string pattern, bool matchCase, string testText = "")
    {
        _pattern = pattern;
        _matchCase = matchCase;
        _testText = testText;
        Evaluate();
    }

    public ObservableCollection<RegexTestMatchViewModel> Matches { get; } = new();

    public string MatchSummaryText =>
        Matches.Count switch
        {
            0 => "No matches",
            1 => "1 match",
            _ => $"{Matches.Count:n0} matches",
        };

    partial void OnPatternChanged(string value) => Evaluate();

    partial void OnTestTextChanged(string value) => Evaluate();

    partial void OnReplacementChanged(string value) => Evaluate();

    partial void OnMatchCaseChanged(bool value) => Evaluate();

    partial void OnMultilineChanged(bool value) => Evaluate();

    partial void OnSinglelineChanged(bool value) => Evaluate();

    private void Evaluate()
    {
        Matches.Clear();
        SelectedMatch = null;
        ReplacementPreview = string.Empty;

        if (string.IsNullOrEmpty(Pattern))
        {
            IsPatternValid = true;
            StatusText = "Enter a pattern";
            OnPropertyChanged(nameof(MatchSummaryText));
            return;
        }

        Regex regex;
        try
        {
            regex = new Regex(Pattern, BuildOptions(), s_timeout);
        }
        catch (ArgumentException ex)
        {
            IsPatternValid = false;
            StatusText = ex.Message;
            OnPropertyChanged(nameof(MatchSummaryText));
            return;
        }

        IsPatternValid = true;

        try
        {
            var matchNumber = 0;
            foreach (Match match in regex.Matches(TestText).Cast<Match>())
            {
                matchNumber++;
                if (Matches.Count < MaxMatches)
                    Matches.Add(RegexTestMatchViewModel.FromMatch(matchNumber, match, regex.GetGroupNames()));
            }

            ReplacementPreview = string.IsNullOrEmpty(Replacement)
                ? string.Empty
                : regex.Replace(TestText, Replacement);

            StatusText = matchNumber switch
            {
                0 => "Pattern is valid; no matches",
                1 => "Pattern is valid; 1 match",
                _ when matchNumber > MaxMatches => $"Pattern is valid; {matchNumber:n0} matches ({MaxMatches:n0} shown)",
                _ => $"Pattern is valid; {matchNumber:n0} matches",
            };
        }
        catch (RegexMatchTimeoutException)
        {
            IsPatternValid = false;
            StatusText = "Regex timed out after 2 seconds.";
            ReplacementPreview = string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            IsPatternValid = false;
            StatusText = ex.Message;
            ReplacementPreview = string.Empty;
        }

        SelectedMatch = Matches.FirstOrDefault();
        OnPropertyChanged(nameof(MatchSummaryText));
    }

    private RegexOptions BuildOptions()
    {
        var options = RegexOptions.CultureInvariant;
        if (!MatchCase)
            options |= RegexOptions.IgnoreCase;
        if (Multiline)
            options |= RegexOptions.Multiline;
        if (Singleline)
            options |= RegexOptions.Singleline;
        return options;
    }
}

public sealed record RegexTestMatchViewModel(
    int Number,
    int Index,
    int Length,
    string Value,
    IReadOnlyList<RegexTestGroupViewModel> Groups)
{
    public string Summary => $"#{Number} at {Index:n0}, length {Length:n0}";

    public string DisplayValue => string.IsNullOrEmpty(Value) ? "(empty match)" : Value;

    public static RegexTestMatchViewModel FromMatch(int number, Match match, string[] groupNames)
    {
        var groups = new List<RegexTestGroupViewModel>();
        for (var i = 0; i < match.Groups.Count; i++)
        {
            var group = match.Groups[i];
            groups.Add(new RegexTestGroupViewModel(
                groupNames.Length > i ? groupNames[i] : i.ToString(CultureInfo.InvariantCulture),
                i,
                group.Success,
                group.Index,
                group.Length,
                group.Value,
                group.Captures.Count));
        }

        return new RegexTestMatchViewModel(number, match.Index, match.Length, match.Value, groups);
    }
}

public sealed record RegexTestGroupViewModel(
    string Name,
    int Number,
    bool Success,
    int Index,
    int Length,
    string Value,
    int CaptureCount)
{
    public string Label => Number == 0 ? "Match" : $"{Name} ({Number})";

    public string Location => Success ? $"{Index:n0}:{Length:n0}" : "Not matched";

    public string DisplayValue => Success
        ? string.IsNullOrEmpty(Value) ? "(empty)" : Value
        : string.Empty;
}
