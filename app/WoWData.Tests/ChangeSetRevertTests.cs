using System.IO;
using System.Linq;
using WoWData.Cli;
using WoWData.Core.Blp;
using WoWData.Core.Workspace;

namespace WoWData.Tests;

/// <summary>
/// 0.14.0: "Revert this import" puts each file back to the version it had just before that change set (the earlier edit
/// when the change set replaced one, the game's original otherwise); other change sets stay; when a NEWER change set
/// changed one of its files again, nothing is reverted. Against a FAKE client folder (the real Client\Data listing is
/// compared before and after); the command line (<c>wds changes revert-set</c>) gives the same results.
/// </summary>
public class ChangeSetRevertTests : IDisposable
{
    private const string Boar = @"Creature\Boar\BoarSkinIvory.blp";
    private const string Axe = @"Interface\Icons\INV_Axe_09.blp";
    internal const string Mark = @"Interface\Icons\INV_Misc_QuestionMark.blp";
    private readonly string _root = TestEnv.TempDir("cs_revert");
    private readonly List<(string, long, DateTime)> _before = TestEnv.DataSnapshot();
    private readonly ClientPaths _paths;
    private readonly Workspace _ws;

    public ChangeSetRevertTests()
    {
        var fakeClient = Path.Combine(_root, "FakeClient");
        Directory.CreateDirectory(Path.Combine(fakeClient, "Data"));
        _paths = new AppPaths(Path.Combine(_root, "home")).ForClient(fakeClient);
        _ws = new Workspace(_paths);
    }

    public void Dispose()
    {
        Assert.Equal(_before, TestEnv.DataSnapshot());    // the real Client\Data folder is unchanged
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A loadable texture that differs from the game's (and from every other <paramref name="n"/>).</summary>
    internal static byte[] Version(string path, int n)
    {
        var orig = BlpFile.Parse(TestEnv.Client.Read(path)!);
        var img = orig.Decode(0);
        for (int i = 0; i < 4 * 64; i += 4) img.Pixels[i] = (byte)(n * 17 + 3);
        img.Pixels[4 * 200] = (byte)n;
        return BlpEncoder.EncodeLike(orig, img).Data;
    }

    private static void Save(Workspace ws, ChangeSet set, string path, byte[] data) =>
        ws.SaveEdit(path, data, new OriginInfo { Sha256 = "x" }, set, null, $"{set.Label}: {Path.GetFileName(path)}");

    private int HistoryFiles() => Directory.Exists(_paths.HistoryDir) ? Directory.EnumerateFiles(_paths.HistoryDir, "*", SearchOption.AllDirectories).Count() : 0;

    [Fact]
    public void EditThenConvertThenRevertGivesBackTheEdit()
    {
        var edit = Version(Boar, 1);
        var a = _ws.BeginChangeSet("my boar");
        Save(_ws, a, Boar, edit);
        // a later import (a map conversion, an update import) replaces the edited file and adds one of its own
        var b = _ws.BeginChangeSet("Map conversion: test");
        Save(_ws, b, Boar, Version(Boar, 2));
        Save(_ws, b, Axe, Version(Axe, 2));
        Assert.Equal(b.Id, _ws.Get(Boar)!.ChangeSet);
        Assert.False(_ws.Manifest.ChangeSets.ContainsKey(a.Id));     // the earlier set has no files left (as before)
        Assert.Equal(1, HistoryFiles());
        // the earlier version survives a restart (the app and wds read changes.json again)
        var ws = new Workspace(_paths);
        Assert.Equal(a.Id, ws.Get(Boar)!.Prior!.Record.ChangeSet);
        var plan = ws.PlanRevertChangeSet(b.Id)!;
        Assert.True(plan.CanRevert);
        Assert.Equal((1, 1), (plan.ToEarlier, plan.ToOriginal));
        Assert.Contains("1 to your earlier edit", plan.Text);
        var done = ws.RevertChangeSet(b.Id);
        Assert.Contains(done, d => d.Contains("put back to your earlier edit (\"my boar\")"));
        // the edit is back, in its own change set again; the file the import added is gone; nothing is left behind
        Assert.Equal(edit, ws.ReadEdited(Boar));
        var rec = ws.Get(Boar)!;
        Assert.Equal(a.Id, rec.ChangeSet);
        Assert.Equal(Workspace.Sha256(edit), rec.WorkspaceSha256);
        Assert.Null(rec.Prior);
        Assert.Equal("my boar", ws.Manifest.ChangeSets[a.Id].Label);
        Assert.Equal(new[] { Workspace.Key(Boar) }, ws.Manifest.ChangeSets[a.Id].Files);
        Assert.False(ws.IsModified(Axe));
        Assert.False(ws.Manifest.ChangeSets.ContainsKey(b.Id));
        Assert.Equal(0, HistoryFiles());
        // and that edit reverts to the game's original as before
        ws.RevertChangeSet(a.Id);
        Assert.Empty(ws.Manifest.Files);
        Assert.Empty(ws.Manifest.ChangeSets);
    }

    [Fact]
    public void WithoutAnEarlierEditTheOriginalComesBack()
    {
        var b = _ws.BeginChangeSet("import");
        Save(_ws, b, Boar, Version(Boar, 1));
        Save(_ws, b, Boar, Version(Boar, 2));      // the same import writing a file twice keeps no version of its own
        Assert.Null(_ws.Get(Boar)!.Prior);
        Assert.Equal(0, HistoryFiles());
        var plan = _ws.PlanRevertChangeSet(b.Id)!;
        Assert.Equal("Each file goes back to the game's original.", plan.Text);
        Assert.Contains(_ws.RevertChangeSet(b.Id), d => d.Contains("the game's original is used again"));
        Assert.False(_ws.IsModified(Boar));
        Assert.Null(_ws.ReadEdited(Boar));
        Assert.Empty(_ws.Manifest.ChangeSets);
    }

    [Fact]
    public void ANewerImportOfTheSameFileStopsTheRevertUntilItIsRevertedFirst()
    {
        var a = _ws.BeginChangeSet("A: my edit");
        Save(_ws, a, Boar, Version(Boar, 1));
        Save(_ws, a, Mark, Version(Mark, 1));       // A keeps a file of its own, so it stays in My changes
        var b = _ws.BeginChangeSet("B: conversion");
        Save(_ws, b, Boar, Version(Boar, 2));
        Save(_ws, b, Axe, Version(Axe, 2));
        var c = _ws.BeginChangeSet("C: newer edit");
        Save(_ws, c, Boar, Version(Boar, 3));
        Assert.Equal(2, HistoryFiles());
        // B: its boar was changed again by C, so nothing of B is reverted (not even the axe C did not touch)
        var plan = _ws.PlanRevertChangeSet(b.Id)!;
        Assert.False(plan.CanRevert);
        Assert.Contains(plan.Problems, p => p.StartsWith(Boar) && p.Contains("changed again later by \"C: newer edit\""));
        var e = Assert.Throws<WorkspaceException>(() => _ws.RevertChangeSet(b.Id));
        Assert.Contains("\"B: conversion\" was not reverted; nothing was changed", e.Message);
        Assert.Contains("Revert the newer import first", e.Message);
        Assert.Equal(Version(Boar, 3), _ws.ReadEdited(Boar));
        Assert.Equal(Version(Axe, 2), _ws.ReadEdited(Axe));
        Assert.Equal(c.Id, _ws.Get(Boar)!.ChangeSet);
        // A: its boar was changed later by B, then C; its own file stays too
        var ea = Assert.Throws<WorkspaceException>(() => _ws.RevertChangeSet(a.Id));
        Assert.Contains("changed again later by \"B: conversion\", then \"C: newer edit\"", ea.Message);
        Assert.Equal(Version(Mark, 1), _ws.ReadEdited(Mark));
        Assert.Equal(2, HistoryFiles());
        // the app asks nothing: it shows the refusal
        var (msg, refused) = ChangeOperations.RevertQuestion(_ws, null, b.Id);
        Assert.True(refused);
        Assert.Contains("was not reverted", msg);
        // newest first: C, then B, then A, each one step back
        _ws.RevertChangeSet(c.Id);
        Assert.Equal(Version(Boar, 2), _ws.ReadEdited(Boar));
        Assert.Equal(b.Id, _ws.Get(Boar)!.ChangeSet);
        Assert.False(ChangeOperations.RevertQuestion(_ws, null, b.Id).Refused);
        _ws.RevertChangeSet(b.Id);
        Assert.Equal(Version(Boar, 1), _ws.ReadEdited(Boar));
        Assert.False(_ws.IsModified(Axe));
        Assert.Equal(new[] { Workspace.Key(Mark), Workspace.Key(Boar) }, _ws.Manifest.ChangeSets[a.Id].Files);
        _ws.RevertChangeSet(a.Id);
        Assert.Empty(_ws.Manifest.Files);
        Assert.Equal(0, HistoryFiles());
    }

    [Fact]
    public void AnImportWhoseFilesWereAllReplacedHasNothingLeftToRevert()
    {
        var a = _ws.BeginChangeSet("A");
        Save(_ws, a, Boar, Version(Boar, 1));
        var b = _ws.BeginChangeSet("B");
        Save(_ws, b, Boar, Version(Boar, 2));
        // as before 0.14.0: A is no longer listed; reverting it does nothing (its version comes back when B is reverted)
        Assert.Null(_ws.PlanRevertChangeSet(a.Id));
        Assert.Empty(_ws.RevertChangeSet(a.Id));
        Assert.Equal(Version(Boar, 2), _ws.ReadEdited(Boar));
    }

    [Fact]
    public void RevertingOneFileGoesToTheOriginalAndDropsItsEarlierVersions()
    {
        var a = _ws.BeginChangeSet("A");
        Save(_ws, a, Boar, Version(Boar, 1));
        var b = _ws.BeginChangeSet("B");
        Save(_ws, b, Boar, Version(Boar, 2));
        Assert.Contains("thrown away too", ChangeOperations.RevertQuestion(_ws, Boar, null).Message);
        _ws.Revert(Boar);
        Assert.False(_ws.IsModified(Boar));
        Assert.Empty(_ws.Manifest.ChangeSets);
        Assert.Equal(0, HistoryFiles());
        Assert.False(Directory.Exists(_paths.HistoryDir));
    }

    [Fact]
    public void TheLooseCopyOfTheRevertedVersionLeavesTheGameAndThePatchMarkStays()
    {
        var a = _ws.BeginChangeSet("A");
        Save(_ws, a, Boar, Version(Boar, 1));
        var b = _ws.BeginChangeSet("B");
        Save(_ws, b, Boar, Version(Boar, 2));
        _ws.InstallLoose(new[] { Boar });
        _ws.BuildPatch("patch-3.MPQ", install: false);
        var dest = _ws.Get(Boar)!.Installed!.Dest;
        Assert.True(File.Exists(dest));
        var done = _ws.RevertChangeSet(b.Id);
        Assert.False(File.Exists(dest));                 // the game no longer sees the reverted version
        Assert.Contains(done, d => d.Contains("install it again to test it in the game") && d.Contains("rebuild patch-3.MPQ"));
        var view = _ws.View(_ws.Get(Boar)!);
        Assert.False(view.InstalledLoose);
        Assert.True(view.Packed);
        Assert.False(view.PackedCurrent);                // "in patch-3.MPQ (older version - rebuild)"
    }

    [Fact]
    public void OnlyTheLatestVersionsOfAFileAreKept()
    {
        var sets = new List<ChangeSet>();
        for (int i = 1; i <= Workspace.MaxPriorVersions + 2; i++)
        {
            var s = _ws.BeginChangeSet($"edit {i}");
            Save(_ws, s, Boar, Version(Boar, i));
            sets.Add(s);
        }
        Assert.Equal(Workspace.MaxPriorVersions, HistoryFiles());
        // the newest ten imports step back one by one; the oldest kept version cannot go further back
        for (int i = sets.Count - 1; i >= 2; i--)
        {
            _ws.RevertChangeSet(sets[i].Id);
            Assert.Equal(Version(Boar, i), _ws.ReadEdited(Boar));
        }
        Assert.True(_ws.Get(Boar)!.PriorDropped);
        var e = Assert.Throws<WorkspaceException>(() => _ws.RevertChangeSet(sets[1].Id));
        Assert.Contains("no longer kept", e.Message);
        _ws.Revert(Boar);                                // Revert on the file still works
        Assert.Empty(_ws.Manifest.Files);
    }
}

/// <summary>"wds changes revert-set" does the same: back to the earlier edit, refused (exit 4) after a newer import.</summary>
[Collection(IndexCollection.Name)]
public class ChangeSetRevertCliTests
{
    private const string Boar = @"Creature\Boar\BoarSkinIvory.blp";

    [Fact]
    public void RevertSetPutsTheEarlierEditBackAndRefusesWhenANewerImportChangedTheFile()
    {
        using var env = new CliTestEnv("revert_set");
        var ws = new Workspace(env.Paths);
        var a = ws.BeginChangeSet("A: my edit");
        ws.SaveEdit(Boar, ChangeSetRevertTests.Version(Boar, 1), new OriginInfo(), a);
        ws.SaveEdit(ChangeSetRevertTests.Mark, ChangeSetRevertTests.Version(ChangeSetRevertTests.Mark, 1), new OriginInfo(), a);
        var b = ws.BeginChangeSet("B: update import");
        ws.SaveEdit(Boar, ChangeSetRevertTests.Version(Boar, 2), new OriginInfo(), b);
        // the dry run says where the file goes
        var dry = env.Json("changes", "revert-set", b.Id, "--dry-run");
        Assert.Equal(0, dry.Code);
        Assert.Equal(1, dry.Data.GetProperty("plan").GetProperty("toEarlier").GetInt32());
        Assert.Equal("A: my edit", dry.Data.GetProperty("plan").GetProperty("steps")[0].GetProperty("earlierImport").GetString());
        Assert.Contains("back to your earlier edit from \"A: my edit\"", env.Run("changes", "revert-set", b.Id, "--dry-run").Out);
        // A was changed later by B: refused for safety, nothing changed
        var refused = env.Run("changes", "revert-set", a.Id, "--yes");
        Assert.Equal(ExitCode.Refused, refused.Code);
        Assert.Contains("changed again later by \"B: update import\"", refused.Err);
        Assert.Equal(ChangeSetRevertTests.Version(Boar, 2), new Workspace(env.Paths).ReadEdited(Boar));
        // B: back to A's edit
        Assert.Equal(ExitCode.NeedsYes, env.Run("changes", "revert-set", b.Id).Code);
        var rev = env.Json("changes", "revert-set", b.Id, "--yes");
        Assert.True(rev.Code == 0, rev.ToString());
        var after = new Workspace(env.Paths);
        Assert.Equal(ChangeSetRevertTests.Version(Boar, 1), after.ReadEdited(Boar));
        Assert.Equal(a.Id, after.Get(Boar)!.ChangeSet);
        Assert.Equal(0, env.Run("changes", "revert-set", a.Id, "--yes").Code);
        Assert.Empty(new Workspace(env.Paths).Manifest.Files);
    }
}
