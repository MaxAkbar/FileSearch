namespace FileSearch.Gui.Settings;

public enum AppShortcutAction
{
    FocusQuery,
    FocusFolder,
    StartSearch,
    CancelSearch,
    FocusResults,
    TogglePreviewPane,
    OpenSelectedResult,
    RevealSelectedResult,
    CopySelectedResultPath,
    PinSelectedResult,
    FavoriteSelectedResult,
    RenameSelectedResult,
    DeleteSelectedResult,
    SaveWorkspace,
    ClearResultFacets,
}

public enum AppShortcutGesture
{
    Disabled,
    CtrlF,
    CtrlL,
    CtrlEnter,
    Escape,
    CtrlR,
    F8,
    Enter,
    CtrlO,
    CtrlE,
    CtrlShiftC,
    CtrlP,
    CtrlShiftS,
    F2,
    Delete,
    CtrlShiftW,
    CtrlShiftBackspace,
}

public sealed class AppShortcutSettings
{
    public AppShortcutGesture FocusQuery { get; set; } = AppShortcutGesture.CtrlF;

    public AppShortcutGesture FocusFolder { get; set; } = AppShortcutGesture.CtrlL;

    public AppShortcutGesture StartSearch { get; set; } = AppShortcutGesture.CtrlEnter;

    public AppShortcutGesture CancelSearch { get; set; } = AppShortcutGesture.Escape;

    public AppShortcutGesture FocusResults { get; set; } = AppShortcutGesture.CtrlR;

    public AppShortcutGesture TogglePreviewPane { get; set; } = AppShortcutGesture.F8;

    public AppShortcutGesture OpenSelectedResult { get; set; } = AppShortcutGesture.Enter;

    public AppShortcutGesture RevealSelectedResult { get; set; } = AppShortcutGesture.CtrlE;

    public AppShortcutGesture CopySelectedResultPath { get; set; } = AppShortcutGesture.CtrlShiftC;

    public AppShortcutGesture PinSelectedResult { get; set; } = AppShortcutGesture.CtrlP;

    public AppShortcutGesture FavoriteSelectedResult { get; set; } = AppShortcutGesture.CtrlShiftS;

    public AppShortcutGesture RenameSelectedResult { get; set; } = AppShortcutGesture.F2;

    public AppShortcutGesture DeleteSelectedResult { get; set; } = AppShortcutGesture.Delete;

    public AppShortcutGesture SaveWorkspace { get; set; } = AppShortcutGesture.CtrlShiftW;

    public AppShortcutGesture ClearResultFacets { get; set; } = AppShortcutGesture.CtrlShiftBackspace;

    public static AppShortcutSettings CreateDefaults() => new();
}
