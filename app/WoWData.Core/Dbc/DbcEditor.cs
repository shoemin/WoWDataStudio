namespace WoWData.Core.Dbc;

/// <summary>One step of the undo history.</summary>
public abstract class DbcChange
{
    public string Label { get; init; } = "";
    internal abstract void Undo(DbcTable t);
    internal abstract void Redo(DbcTable t);
}

/// <summary>New values for some cells (one edit, a bulk edit or the changed rows of an import).</summary>
public sealed class DbcCellChange : DbcChange
{
    public sealed record Cell(DbcRow Row, int Field, DbcCell Before, DbcCell After);

    public List<Cell> Cells { get; } = new();

    internal override void Undo(DbcTable t)
    {
        for (int i = Cells.Count - 1; i >= 0; i--) t.SetCell(Cells[i].Row, Cells[i].Field, Cells[i].Before);
    }

    internal override void Redo(DbcTable t)
    {
        foreach (var c in Cells) t.SetCell(c.Row, c.Field, c.After);
    }
}

/// <summary>Rows added and / or removed (add row, duplicate, delete, the added and removed rows of an import).</summary>
public sealed class DbcRowsChange : DbcChange
{
    /// <summary>(row, index it was at) of removed rows, in increasing index order.</summary>
    public List<(DbcRow Row, int Index)> Removed { get; } = new();
    /// <summary>(row, index it is inserted at) of added rows, in the order they were inserted.</summary>
    public List<(DbcRow Row, int Index)> Added { get; } = new();

    internal override void Undo(DbcTable t)
    {
        for (int i = Added.Count - 1; i >= 0; i--) t.Rows.RemoveAt(Added[i].Index);
        foreach (var (row, idx) in Removed) t.Rows.Insert(idx, row);
        t.Touch();
    }

    internal override void Redo(DbcTable t)
    {
        for (int i = Removed.Count - 1; i >= 0; i--) t.Rows.RemoveAt(Removed[i].Index);
        foreach (var (row, idx) in Added) t.Rows.Insert(idx, row);
        t.Touch();
    }
}

/// <summary>Several changes as one undo step (an import: changed cells, then added and removed rows).</summary>
public sealed class DbcCompositeChange : DbcChange
{
    public List<DbcChange> Parts { get; } = new();

    internal override void Undo(DbcTable t)
    {
        for (int i = Parts.Count - 1; i >= 0; i--) Parts[i].Undo(t);
    }

    internal override void Redo(DbcTable t)
    {
        foreach (var p in Parts) p.Redo(t);
    }
}

/// <summary>
/// Edits of one table with undo and redo. Every edit goes through here: one cell, a bulk edit of many rows, adding,
/// duplicating and deleting rows, an import. The table is "dirty" until it is saved (the save point is remembered, so
/// undoing back to it makes the table clean again).
/// </summary>
public sealed class DbcEditor
{
    private readonly List<DbcChange> _undo = new();
    private readonly List<DbcChange> _redo = new();
    private int _savedAt;
    private bool _savedLost;

    public DbcEditor(DbcTable table) => Table = table;

    public DbcTable Table { get; }

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;
    public IReadOnlyList<DbcChange> History => _undo;

    /// <summary>True when the table differs from what was loaded or last saved.</summary>
    public bool IsDirty => _savedLost || _undo.Count != _savedAt;

    public void MarkSaved()
    {
        _savedAt = _undo.Count;
        _savedLost = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Push(DbcChange c)
    {
        if (_savedAt > _undo.Count) _savedLost = true;   // the saved state was in the redo list, now gone
        _undo.Add(c);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var c = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        c.Undo(Table);
        _redo.Add(c);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var c = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        c.Redo(Table);
        _undo.Add(c);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Set cells (only the ones whose value really changes are recorded). Returns how many changed.</summary>
    public int SetCells(IEnumerable<(DbcRow Row, int Field, DbcCell Value)> cells, string label)
    {
        var ch = new DbcCellChange { Label = label };
        foreach (var (row, field, value) in cells)
        {
            var before = Table.GetCell(row, field);
            if (before.SameAs(value)) continue;
            Table.SetCell(row, field, value);
            ch.Cells.Add(new DbcCellChange.Cell(row, field, before, value));
        }
        if (ch.Cells.Count == 0) return 0;
        Push(ch);
        return ch.Cells.Count;
    }

    public int SetCell(DbcRow row, int field, DbcCell value, string label) => SetCells(new[] { (row, field, value) }, label);

    /// <summary>Add a row (inserted in ID order when the table is sorted by ID). Returns its index.</summary>
    public int AddRow(DbcRow row, string label)
    {
        int idx = Table.InsertSorted(row);
        var ch = new DbcRowsChange { Label = label };
        ch.Added.Add((row, idx));
        Push(ch);
        return idx;
    }

    /// <summary>A copy of <paramref name="source"/> with a new ID ("duplicate as new").</summary>
    public DbcRow Duplicate(DbcRow source, long newId)
    {
        var copy = source.Clone();
        if (Table.Layout.KeyColumn is { } k) copy.F[k.Field] = unchecked((uint)newId);
        AddRow(copy, $"Duplicate {Table.Label(source)} as {(Table.Layout.HasKey ? "ID " + newId : "a new row")}");
        return copy;
    }

    /// <summary>
    /// 0.13.2: move a row to another place in the file (one undo step); <paramref name="index"/> is its index after the
    /// move. The order of rows matters to the game for some tables: the 1.12 client reads each talent tree's Talent.dbc
    /// rows as ONE run of consecutive rows (TalentInfo_Initialize), so the talent designer keeps a tree's rows together.
    /// </summary>
    public void MoveRow(DbcRow row, int index, string label)
    {
        int from = Table.Rows.IndexOf(row);
        if (from < 0) throw new DbcException("The row is not in the table.");
        index = Math.Clamp(index, 0, Table.Rows.Count - 1);
        if (index == from) return;
        Table.Rows.RemoveAt(from);
        Table.Rows.Insert(index, row);
        Table.Touch();
        var ch = new DbcRowsChange { Label = label };
        ch.Removed.Add((row, from));
        ch.Added.Add((row, index));
        Push(ch);
    }

    public void DeleteRows(IEnumerable<DbcRow> rows, string label)
    {
        var set = rows.ToHashSet();
        if (set.Count == 0) return;
        var ch = new DbcRowsChange { Label = label };
        for (int i = 0; i < Table.Rows.Count; i++)
            if (set.Contains(Table.Rows[i])) ch.Removed.Add((Table.Rows[i], i));
        for (int i = ch.Removed.Count - 1; i >= 0; i--) Table.Rows.RemoveAt(ch.Removed[i].Index);
        Table.Touch();
        Push(ch);
    }

    /// <summary>Apply an import plan as one undo step.</summary>
    public void Apply(DbcImportPlan plan, string label)
    {
        if (plan.Errors.Count > 0) throw new DbcException("The import has errors; fix them first.");
        var all = new DbcCompositeChange { Label = label };
        var cells = new DbcCellChange { Label = label };
        foreach (var r in plan.Changed)
            foreach (var (field, value) in r.Values)
            {
                var before = Table.GetCell(r.Target, field);
                if (before.SameAs(value)) continue;
                Table.SetCell(r.Target, field, value);
                cells.Cells.Add(new DbcCellChange.Cell(r.Target, field, before, value));
            }
        if (cells.Cells.Count > 0) all.Parts.Add(cells);
        if (plan.Removed.Count > 0)
        {
            var rem = new DbcRowsChange { Label = label };
            var set = plan.Removed.ToHashSet();
            for (int i = 0; i < Table.Rows.Count; i++)
                if (set.Contains(Table.Rows[i])) rem.Removed.Add((Table.Rows[i], i));
            for (int i = rem.Removed.Count - 1; i >= 0; i--) Table.Rows.RemoveAt(rem.Removed[i].Index);
            Table.Touch();
            all.Parts.Add(rem);
        }
        if (plan.Added.Count > 0)
        {
            var add = new DbcRowsChange { Label = label };
            foreach (var r in plan.Added)
            {
                int idx = Table.InsertSorted(r);
                add.Added.Add((r, idx));
            }
            all.Parts.Add(add);
        }
        if (all.Parts.Count == 0) return;
        Push(all);
    }
}
