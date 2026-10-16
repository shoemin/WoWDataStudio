using System.IO;
using System.Windows;
using WoWData.Core.Dbc;
using WoWDataStudio.ViewModels;
using WoWDataStudio.Views;

namespace WoWDataStudio.Services;

/// <summary>
/// Part of `--screenshot` (0.7.0, the DBC editor), in this run's own app folder (the edits go into the run's workspace;
/// "Copy to server" copies into a FAKE server folder inside the run's folder, never the real server):
///   83_tables_spell              the Data tables tab: Spell, Fireball selected, its icon through SpellIconID
///   84_tables_link_used_by       SpellIcon 185 after following the link, with "Used by"
///   85_tables_validation         a refused edit (a link to a row that does not exist)
///   86_tables_save               "Save to my workspace": the changes before saving
///   87_tables_my_changes         My changes with the edited table
///   88_tables_compare            Compare: the table row by row
///   89_tables_item_display       ItemDisplayInfo with a column filter, the icon and "Show in 3D"
///   90_tables_bulk_edit          the bulk edit window
///   91_tables_taxi_node          TaxiNodes: a flight point duplicated as a new row and renamed
///   92_tables_creature_display   CreatureDisplayInfo: a creature display copied, with its skin
///   93_tables_import_preview     importing a CSV: changed, added rows and an error
///   94_tables_server_copy        "Copy tables to the server" (fake server folder)
/// </summary>
internal static class DbcScreenshots
{
    private static void Expect(bool ok, string what) => ScreenshotRunner.Expect(ok, what);

    private static async Task Open(DbcEditorViewModel t, string table)
    {
        var item = t.AllTables.First(x => x.Name == table);
        await t.SelectTableAsync(item);
        await ScreenshotRunner.Until(() => t.Table?.Name == table && !t.IsLoading, 60000);
        await ScreenshotRunner.Settle(300);
    }

    private static async Task Select(DbcEditorViewModel t, long id)
    {
        Expect(t.GoTo(id), $"tables: {t.Table?.Name} has ID {id}");
        await ScreenshotRunner.Until(() => t.SelectedRow != null && t.Table!.Key(t.SelectedRow.Row) == id, 30000);
        await ScreenshotRunner.Until(() => !t.UsedByLoading, 30000);
        if (t.HasPreview) await ScreenshotRunner.Until(() => t.Preview.IsLoaded || t.Preview.Error.Length > 0, 30000);
        await ScreenshotRunner.Settle(500);
    }

    private static int Col(DbcEditorViewModel t, string name) => t.Table!.Layout.Column(name)!.Index;

    private static Task Shot(MainWindow win, string outDir, string name)
    {
        ScreenshotRunner.Main!.ToastVisible = false;
        return ScreenshotRunner.Shot(win, outDir, name);
    }

    /// <summary>A window's content drawn on its own at the given size (an off-screen window does not always lay its
    /// content out at the size asked for).</summary>
    private static async Task ShotWindow(Window w, double width, double height, string outDir, string name)
    {
        var content = (FrameworkElement)w.Content;
        w.Content = null;
        var host = new System.Windows.Controls.Border { Child = content, Width = width, Height = height };
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WindowBg");
        var hw = new Window
        {
            Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, Left = -30000, Top = 0,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        hw.SetResourceReference(FrameworkElement.StyleProperty, "AppWindow");
        hw.Show();
        await ScreenshotRunner.Shot(hw, outDir, name);
        hw.Close();
        host.Child = null;
        w.Content = content;
    }

    public static async Task RunAsync(MainWindow win, BrowserViewModel b, string outDir)
    {
        var session = ScreenshotRunner.Main!.Session;
        var t = b.Tables;
        ScreenshotRunner.Place(win, 1680, 1050);
        win.UpdateLayout();
        b.SelectedTab = BrowserViewModel.TablesTab;
        await t.EnsureLoadedAsync();
        await ScreenshotRunner.Until(() => t.IsLoaded && t.Table != null && !t.IsLoading, 60000);
        Expect(t.AllTables.Count == 158 && t.AllTables.Count(x => x.Info.DeletedInGame) == 4, $"tables: {t.AllTables.Count} tables listed (154 the game loads, 4 deleted by patch.MPQ)");
        var svc = session.Dbc!;

        // ---- 83: Spell, Fireball, its icon through the SpellIconID link
        await Open(t, "Spell");
        Expect(t.Rows.Count == 22357, $"tables: Spell has {t.Rows.Count} rows");
        t.SearchText = "fireball";
        await t.ApplyFilterNow();
        await ScreenshotRunner.Until(() => t.Rows.Count < 200 && !t.IsLoading, 60000);
        Expect(t.Rows.Count > 5, $"tables: searching fireball finds {t.Rows.Count} spells");
        await Select(t, 133);
        Expect(t.DetailTitle == "Fireball", $"tables: Spell 133 is {t.DetailTitle}");
        Expect(t.PreviewPath.EndsWith("Spell_Fire_FlameBolt.blp", StringComparison.OrdinalIgnoreCase), $"tables: the spell's icon is shown through SpellIconID ({t.PreviewPath})");
        var school = t.Fields.First(f => f.Name == "School");
        Expect(school.IsEnum && school.Choice?.Text == "2 · Fire", "tables: School is shown as a named choice (2 · Fire)");
        Expect(t.Fields.First(f => f.Name == "SpellIconID").LinkText.Contains("Spell_Fire_FlameBolt"), "tables: SpellIconID links to SpellIcon 185");
        await Shot(win, outDir, "83_tables_spell");

        // ---- 84: follow the link to SpellIcon, "Used by"
        t.Fields.First(f => f.Name == "SpellIconID").FollowLinkCommand.Execute(null);
        await ScreenshotRunner.Until(() => t.Table?.Name == "SpellIcon" && t.SelectedRow != null && !t.IsLoading, 30000);
        await ScreenshotRunner.Until(() => !t.UsedByLoading && t.UsedBy.Count > 0, 60000);
        await ScreenshotRunner.Until(() => t.Preview.IsLoaded || t.Preview.Error.Length > 0, 30000);
        await ScreenshotRunner.Settle(500);
        Expect(t.UsedBy.Any(u => u.Table == "Spell" && u.Id == 133), $"tables: Used by of SpellIcon 185 lists Fireball ({t.UsedBy.Count} rows)");
        Expect(t.CanGoBack, "tables: Back is available after following a link");
        await Shot(win, outDir, "84_tables_link_used_by");

        // ---- 85: a refused edit; then the spell's new icon (the manual's first example)
        await t.BackCommand.ExecuteAsync(null);
        await ScreenshotRunner.Until(() => t.Table?.Name == "Spell" && t.SelectedRow != null, 30000);
        await Select(t, 133);
        var fireball = t.SelectedRow!;
        Expect(!t.TryEditCell(fireball, Col(t, "SpellIconID"), "999999") && t.StatusIsError && t.Status.Contains("There is no SpellIcon with ID 999999"),
            "tables: a link to a row that does not exist is refused");
        var iconField = t.Fields.First(f => f.Name == "SpellIconID");
        iconField.Text = "999999";
        iconField.CommitCommand.Execute(null);
        await ScreenshotRunner.Settle(400);
        Expect(iconField.Problem.Contains("There is no SpellIcon"), "tables: the details panel says why the value was refused");
        view(win).ShowField("SpellIconID");
        await Shot(win, outDir, "85_tables_validation");
        var icons = svc.Lookup("SpellIcon")!;
        var newIcon = icons.Rows.First(r => icons.Text(r, 1).EndsWith("Spell_Fire_Fireball02", StringComparison.OrdinalIgnoreCase));
        long newIconId = icons.Key(newIcon)!.Value;
        Expect(t.TryEditCell(fireball, Col(t, "SpellIconID"), newIconId.ToString()), $"tables: Fireball gets icon {newIconId}");
        Expect(t.IsDirty && t.DirtyText == "Unsaved changes", "tables: the table shows unsaved changes");

        // ---- 86: the changes before saving; save
        var diff = DbcDiff.Compare(svc.LoadGame("Spell"), t.Table!);
        Expect(diff.Changed == 1 && diff.Rows[0].Cells.Single().Column == "SpellIconID", $"tables: one changed value before saving ({diff.Summary})");
        await ShotWindow(new DbcDiffWindow("Save Spell.dbc to my workspace", diff, $"Your version of Spell.dbc compared with the game's file: {diff.Summary}\n\nSaving puts it into your workspace (My changes). Nothing in the game changes until you use Test in game or Build release patch.\n\nThe server reads this data from its database (spell_template), not from this file.",
            "Save to my workspace"), 1100, 640, outDir, "86_tables_save");
        Expect(t.SaveCore(interactive: false), "tables: Spell.dbc saved to the workspace");
        await ScreenshotRunner.Settle(500);
        Expect(session.Workspace!.IsModified(@"DBFilesClient\Spell.dbc") && !t.IsDirty, "tables: My changes lists Spell.dbc");
        var reloaded = svc.LoadCurrent("Spell");
        Expect(reloaded.Number(reloaded.Find(133)!, reloaded.Layout.Column("SpellIconID")!) == newIconId, "tables: the saved table reads back with the new icon");

        // ---- 87, 88: My changes and Compare
        b.SelectedTab = 1;
        await ScreenshotRunner.Settle(700);
        Expect(b.Changes.Any(c => c.Path == @"DBFilesClient\Spell.dbc" && c.Format.StartsWith("DBC, ")), "tables: My changes shows Spell.dbc as a DBC table");
        await Shot(win, outDir, "87_tables_my_changes");
        var cmp = svc.CompareWithGame("Spell");
        await ShotWindow(new DbcDiffWindow("Spell.dbc: your version and the game's", cmp, $"Your version of Spell.dbc (in your workspace) compared with the game's file: {cmp.Summary}", null), 1100, 600, outDir, "88_tables_compare");
        b.SelectedTab = BrowserViewModel.TablesTab;

        // ---- 89: ItemDisplayInfo, a column filter, the icon and "Show in 3D"
        await Open(t, "ItemDisplayInfo");
        t.SetColumnFilter(Col(t, "InventoryIcon"), "INV_Sword_04");
        view(win).Refilter();
        await t.ApplyFilterNow();
        await ScreenshotRunner.Until(() => t.Rows.Count > 0 && t.Rows.Count < 200, 30000);
        // a display that a server item uses (it opens in 3D)
        foreach (var rv in t.Rows.Take(60))
        {
            t.SelectedRow = rv;
            if (t.ModelText.Length > 0) break;
        }
        await ScreenshotRunner.Until(() => !t.UsedByLoading && (t.Preview.IsLoaded || t.Preview.Error.Length > 0), 30000);
        view(win).DataGrid.ScrollIntoView(t.SelectedRow);
        await ScreenshotRunner.Settle(600);
        Expect(t.PreviewPath.EndsWith("INV_Sword_04.blp", StringComparison.OrdinalIgnoreCase), "tables: an item display shows its inventory icon");
        Expect(t.ModelText.StartsWith("Show in 3D", StringComparison.Ordinal), $"tables: an item display can be shown in 3D ({t.ModelText})");
        await Shot(win, outDir, "89_tables_item_display");
        t.ClearFiltersCommand.Execute(null);

        // ---- 90: bulk edit (shown, not applied)
        await Open(t, "SpellCastTimes");
        t.SelectedRowsFromView(t.Rows.Skip(2).Take(4).ToList());
        var bw = new DbcBulkEditWindow(t, t.SelectedRows.Select(r => r.Row).ToList()) { Column = t.Table!.Layout.Column("Base"), Operation = BulkOperation.Multiply, Value = "0.5" };
        await ShotWindow(bw, 560, 470, outDir, "90_tables_bulk_edit");
        var (bulkOk, bulkMsg) = t.BulkEdit(t.Table.Layout.Column("Base")!, BulkOperation.Multiply, "0.5", t.SelectedRows.Select(r => r.Row).ToList());
        Expect(bulkOk && t.CanUndo, $"tables: bulk edit of 4 rows ({bulkMsg})");
        t.UndoCommand.Execute(null);
        await ScreenshotRunner.Settle(300);
        Expect(!t.IsDirty, "tables: undo of the bulk edit leaves the table unchanged");

        // ---- 91: a new flight point (TaxiNodes): duplicate as new, rename
        await Open(t, "TaxiNodes");
        await Select(t, 2);   // Stormwind
        int before = t.Table!.Rows.Count;
        long newNode = t.Table.SuggestId();
        t.DuplicateRowCommand.Execute(null);
        await ScreenshotRunner.Until(() => t.Table.Rows.Count == before + 1 && t.SelectedRow != null && t.Table.Key(t.SelectedRow.Row) == newNode, 30000);
        // typed into the grid's cell, as a user does (double-click, type, Enter)
        Expect(await EditInGrid(win, t, "Name", "Stormwind, Old Town") && t.Table.Text(t.SelectedRow!.Row, t.Table.Layout.Column("Name")!) == "Stormwind, Old Town",
            "tables: the new flight point is renamed in the grid");
        Expect(!await EditInGrid(win, t, "ContinentID", "-5") && t.StatusIsError, "tables: the grid refuses a negative map ID");
        Expect(t.TryEditCell(t.SelectedRow!, Col(t, "X"), "-8749.5"), "tables: its position changes");
        await ScreenshotRunner.Settle(600);
        Expect(t.Rows.Any(r => r.IsChanged), "tables: changed rows are marked in the grid");
        await Shot(win, outDir, "91_tables_taxi_node");
        await t.DiscardCommand.ExecuteAsync(null);
        await ScreenshotRunner.Until(() => !t.IsDirty && !t.IsLoading, 30000);

        // ---- 92: copy a creature display (the Mottled Boar's) as a new one
        await Open(t, "CreatureDisplayInfo");
        await Select(t, 503);
        long newDisplay = t.Table!.SuggestId();
        t.DuplicateRowCommand.Execute(null);
        await ScreenshotRunner.Until(() => t.SelectedRow != null && t.Table.Key(t.SelectedRow.Row) == newDisplay, 30000);
        Expect(t.TryEditCell(t.SelectedRow!, Col(t, "CreatureModelScale"), "1.5"), "tables: the copied display is made bigger");
        await ScreenshotRunner.Until(() => t.Preview.IsLoaded || t.Preview.Error.Length > 0, 30000);
        await ScreenshotRunner.Settle(600);
        Expect(t.PreviewPath.StartsWith(@"Creature\Boar\", StringComparison.OrdinalIgnoreCase), $"tables: the creature display shows its skin ({t.PreviewPath})");
        await Shot(win, outDir, "92_tables_creature_display");
        await t.DiscardCommand.ExecuteAsync(null);
        await ScreenshotRunner.Until(() => !t.IsDirty && !t.IsLoading, 30000);

        // ---- 93: import a CSV with a change, a new row and an error
        await Open(t, "SpellIcon");
        var dir = Path.Combine(outDir, "_tables");
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, "SpellIcon_edit.csv");
        t.ExportTo(csv, t.Table!.Rows.Take(6).ToList());
        var lines = File.ReadAllLines(csv).ToList();
        Expect(lines[0].TrimStart('﻿') == "ID,TextureFilename", "tables: CSV export has the column names");
        var first = lines[1].Split(',')[0];
        lines[1] = $"{first},Interface\\Icons\\INV_Misc_Gem_Ruby_02";
        lines.Add($"{t.Table.SuggestId()},Interface\\Icons\\INV_Misc_QuestionMark");
        lines.Add("abc,Interface\\Icons\\Broken");
        File.WriteAllLines(csv, lines, new System.Text.UTF8Encoding(true));
        var iw = new DbcImportWindow(t, csv);
        var plan = iw.CurrentPlan!;
        Expect(plan.Changed.Count == 1 && plan.Added.Count == 1 && plan.Errors.Count == 1 && !plan.CanApply, $"tables: import preview: {plan.Preview.Summary}, {plan.Errors.Count} error");
        await ShotWindow(iw, 1100, 760, outDir, "93_tables_import_preview");
        File.WriteAllLines(csv, lines.Take(lines.Count - 1), new System.Text.UTF8Encoding(true));
        var good = t.PlanImport(csv, DbcMissingRows.Keep);
        Expect(good.CanApply, "tables: without the broken line the import can be applied");
        t.ApplyImport(good, csv);
        await ScreenshotRunner.Settle(400);
        Expect(t.IsDirty && t.Table.Rows.Count == icons.Rows.Count + 1, "tables: the import added a row and changed one (one undo step)");
        t.UndoCommand.Execute(null);
        await ScreenshotRunner.Settle(300);
        Expect(!t.IsDirty, "tables: undo of the import leaves the table unchanged");

        // ---- 94: copy to server (a FAKE server data folder in the run's folder)
        await Open(t, "TaxiPath");
        await Select(t, t.Table!.Key(t.Table.Rows[0])!.Value);
        Expect(t.TryEditCell(t.SelectedRow!, Col(t, "Cost"), "123"), "tables: a flight route's price changes");
        Expect(t.SaveCore(interactive: false), "tables: TaxiPath.dbc saved");
        var fakeData = Path.Combine(outDir, "_server", "data");
        var fakeDbc = Path.Combine(fakeData, "5875", "dbc");
        Directory.CreateDirectory(fakeDbc);
        File.WriteAllBytes(Path.Combine(fakeDbc, "TaxiPath.dbc"), svc.ReadGame("TaxiPath")!);
        File.WriteAllBytes(Path.Combine(fakeDbc, "Spell.dbc"), svc.ReadGame("Spell")!);
        var keepServer = session.Settings.ServerDataDir;
        session.Settings.ServerDataDir = fakeData;
        try
        {
            var sw = new DbcServerCopyWindow(t, "TaxiPath", () => new List<string>());
            var copy = sw.Service!;
            Expect(copy.ServerDbcDir == fakeDbc, "tables: the server's dbc folder is <server data>\\5875\\dbc");
            var items = copy.Plan(session.Workspace!);
            Expect(items.Single(i => i.Table == "TaxiPath").CanCopy && !items.Single(i => i.Table == "Spell").CanCopy, "tables: TaxiPath is copied, Spell (server database) is not");
            sw.CopyNow();
            var rec = copy.LoadHistory().Last();
            Expect(rec.Written.Count == 1 && File.ReadAllBytes(Path.Combine(fakeDbc, "TaxiPath.dbc")).AsSpan().SequenceEqual(session.Workspace!.ReadEdited(@"DBFilesClient\TaxiPath.dbc")),
                "tables: copy to server wrote TaxiPath.dbc and backed up the old one");
            Expect(File.ReadAllBytes(Path.Combine(fakeDbc, "Spell.dbc")).AsSpan().SequenceEqual(svc.ReadGame("Spell")), "tables: the server's Spell.dbc was not touched");
            await ShotWindow(sw, 900, 640, outDir, "94_tables_server_copy");
        }
        finally
        {
            session.Settings.ServerDataDir = keepServer;
        }
        ScreenshotRunner.Place(win, 1440, 900);
    }

    private static DbcEditorView view(MainWindow win) => win.BrowserScreen.TablesScreen;

    /// <summary>Type a value into a cell of the selected row through the DataGrid's own editing (begin, type, commit).</summary>
    private static async Task<bool> EditInGrid(MainWindow win, DbcEditorViewModel t, string column, string text)
    {
        var grid = view(win).DataGrid;
        var row = t.SelectedRow!;
        var col = grid.Columns[t.Table!.Layout.Column(column)!.Index];
        grid.ScrollIntoView(row, col);
        await ScreenshotRunner.Settle(200);
        grid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(row, col);
        grid.BeginEdit();
        await ScreenshotRunner.Settle(200);
        var box = FindChild<System.Windows.Controls.TextBox>(col.GetCellContent(row));
        if (box == null) return false;
        var before = t.Table.Format(row.Row, t.Table.Layout.Column(column)!, t.Locale);
        box.Text = text;
        grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
        await ScreenshotRunner.Settle(400);
        return t.Table.Format(row.Row, t.Table.Layout.Column(column)!, t.Locale) != before;
    }

    private static T? FindChild<T>(DependencyObject? d) where T : DependencyObject
    {
        if (d == null) return null;
        if (d is T x) return x;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindChild<T>(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }
}
