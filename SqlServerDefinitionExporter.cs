using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;

namespace ExcelToDDLToola_New;

/// <summary>
/// Read-only SQL Server catalog reader and A5-style .xlsx writer. No Excel/A5 installation required.
/// Copy this file and its NuGet dependencies, or reference the net10.0 library.
/// </summary>
public sealed class SqlServerDefinitionExporter
{
    public sealed record ObjectName(string Schema, string Name);

    public sealed class ExportOptions
    {
        public string SystemName { get; init; } = "";
        public string SubsystemName { get; init; } = "";
        public string Author { get; init; } = "";
        // Explicit business dates; database create/modify dates are not document revision dates.
        public DateOnly? CreatedDate { get; init; }
        public DateOnly? UpdatedDate { get; init; }
        public string? Schema { get; init; }
        public IReadOnlyCollection<ObjectName> Objects { get; init; } = Array.Empty<ObjectName>();
        public bool IncludeViews { get; init; } = true;
        public string DescriptionProperty { get; init; } = "MS_Description";
        public string? LogicalNameProperty { get; init; }
        public string? RemarksProperty { get; init; }
        public int CommandTimeoutSeconds { get; init; } = 120;
        public string FontName { get; init; } = "Meiryo UI";
        public bool IncludeSqlDefinitions { get; init; } = false;
    }

    public sealed class DatabaseDefinition
    {
        public string Name { get; set; } = "";
        public string Rdbms { get; set; } = "";
        public List<ObjectDefinition> Objects { get; } = new();
    }

    public sealed class ObjectDefinition
    {
        public int Id { get; set; }
        public string Schema { get; set; } = "dbo";
        public string Name { get; set; } = "";
        public string LogicalName { get; set; } = "";
        public string Remarks { get; set; } = "";
        public bool IsView { get; set; }
        public string? SqlDefinition { get; set; }
        public List<ColumnDefinition> Columns { get; } = new();
        public List<IndexDefinition> Indexes { get; } = new();
        public List<ConstraintDefinition> Constraints { get; } = new();
        public List<ForeignKeyDefinition> ForeignKeys { get; } = new();
        public List<ForeignKeyDefinition> ReferencedBy { get; } = new();
        public List<TriggerDefinition> Triggers { get; } = new();
    }

    public sealed record ColumnDefinition(int Ordinal, string LogicalName, string Name,
        string DataType, bool NotNull, bool PrimaryKey, string DefaultValue, string Remarks);
    public sealed record IndexDefinition(string Name, string Columns, bool PrimaryKey,
        bool Unique, string Remarks);
    public sealed record ConstraintDefinition(string Name, string Type, string Definition);
    public sealed record ForeignKeyDefinition(string Name, string Parent, string ParentColumns,
        string Referenced, string ReferencedColumns, string Remarks);
    public sealed record TriggerDefinition(string Name, string Events, string Timing,
        string Remarks, string? SqlDefinition);

    /// <summary>Reads metadata then atomically replaces one workbook, with one sheet per table/view.</summary>
    public async Task ExportAsync(string connectionString, string outputPath,
        ExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var model = await ReadAsync(connectionString, options, cancellationToken).ConfigureAwait(false);
        // Also suitable for WPF: do not run workbook serialization on the UI thread.
        await Task.Run(() => WriteWorkbook(model, outputPath, options, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Requires database VIEW DEFINITION; never executes DDL/DML or queries user table rows.
    /// Read during a schema-stable period. Separate catalog SELECTs are not a schema snapshot.
    /// </summary>
    public async Task<DatabaseDefinition> ReadAsync(string connectionString,
        ExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.CommandTimeoutSeconds < 1) throw new ArgumentOutOfRangeException(nameof(options));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        async Task<DataTable> Query(string sql)
        {
            await using var command = new SqlCommand(sql, connection)
            { CommandTimeout = options.CommandTimeoutSeconds };
            command.Parameters.Add("@description", SqlDbType.NVarChar, 128).Value = options.DescriptionProperty;
            command.Parameters.Add("@logical", SqlDbType.NVarChar, 128).Value =
                (object?)options.LogicalNameProperty ?? DBNull.Value;
            command.Parameters.Add("@remarks", SqlDbType.NVarChar, 128).Value =
                (object?)options.RemarksProperty ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var result = new DataTable();
            for (var i = 0; i < reader.FieldCount; i++) result.Columns.Add(reader.GetName(i), reader.GetFieldType(i));
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                result.Rows.Add(values);
            }
            return result;
        }

        var info = (await Query("""
            SELECT DB_NAME() AS DatabaseName,
              CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS ProductVersion,
              HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') AS CanRead;
            """)).Rows[0];
        if (Number(info, "CanRead") != 1)
            throw new InvalidOperationException("Database VIEW DEFINITION permission is required; refusing a potentially incomplete report.");
        var model = new DatabaseDefinition
        { Name = Text(info, "DatabaseName"), Rdbms = "Microsoft SQL Server " + Text(info, "ProductVersion") };
        var objects = await Query(ObjectsSql);
        // Object selection uses exact ordinal spelling, avoiding ambiguity in case-sensitive databases.
        var requested = options.Objects.ToHashSet();
        foreach (DataRow row in objects.Rows)
        {
            var name = new ObjectName(Text(row, "SchemaName"), Text(row, "ObjectName"));
            var isView = Text(row, "ObjectType") == "V";
            if (options.Schema is not null && name.Schema != options.Schema) continue;
            if (requested.Count != 0 && !requested.Contains(name)) continue;
            if (isView && !options.IncludeViews) continue;
            model.Objects.Add(new ObjectDefinition
            {
                Id = Number(row, "object_id"), Schema = name.Schema, Name = name.Name,
                IsView = isView, LogicalName = Text(row, "LogicalName"), Remarks = Text(row, "Remarks"),
                SqlDefinition = OptionalText(row, "definition")
            });
        }
        var found = model.Objects.Select(o => new ObjectName(o.Schema, o.Name)).ToHashSet();
        var missing = requested.Except(found).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException("Requested objects missing or excluded: " +
                string.Join(", ", missing.Select(n => n.Schema + "." + n.Name)));
        if (model.Objects.Count == 0) throw new InvalidOperationException("No tables/views matched. No output was written.");
        var byId = model.Objects.ToDictionary(o => o.Id);

        foreach (DataRow row in (await Query(ColumnsSql)).Rows)
        {
            if (!byId.TryGetValue(Number(row, "object_id"), out var obj)) continue;
            var notes = new List<string> { Text(row, "Remarks") };
            if (Flag(row, "is_identity")) notes.Add($"IDENTITY({Text(row, "seed_value")},{Text(row, "increment_value")})");
            if (Flag(row, "is_computed")) notes.Add("AS " + Text(row, "computed_definition") +
                (Flag(row, "is_persisted") ? " PERSISTED" : ""));
            if (Flag(row, "is_rowguidcol")) notes.Add("ROWGUIDCOL");
            if (Flag(row, "is_sparse")) notes.Add("SPARSE");
            if (Flag(row, "is_hidden")) notes.Add("HIDDEN");
            if (Text(row, "generated_always_type_desc") != "NOT_APPLICABLE") notes.Add(Text(row, "generated_always_type_desc"));
            obj.Columns.Add(new ColumnDefinition(Number(row, "column_id"), Text(row, "LogicalName"),
                Text(row, "name"), FormatType(row), !Flag(row, "is_nullable"), Flag(row, "IsPk"),
                Text(row, "default_definition"), JoinNotes(notes)));
        }

        var indexColumns = (await Query(IndexColumnsSql)).AsEnumerable()
            .GroupBy(r => (Number(r, "object_id"), Number(r, "index_id")))
            .ToDictionary(g => g.Key, g => g.ToArray());
        foreach (DataRow row in (await Query(IndexesSql)).Rows)
        {
            var id = Number(row, "object_id");
            if (!byId.TryGetValue(id, out var obj)) continue;
            var cols = indexColumns.GetValueOrDefault((id, Number(row, "index_id"))) ?? Array.Empty<DataRow>();
            var keys = cols.Where(c => Number(c, "key_ordinal") > 0).OrderBy(c => Number(c, "key_ordinal"));
            var keyText = string.Join(",", keys.Select(c => Text(c, "name") + (Flag(c, "is_descending_key") ? " DESC" : "")));
            var include = cols.Where(c => Flag(c, "is_included_column")).OrderBy(c => Number(c, "index_column_id"));
            var includeText = string.Join(", ", include.Select(c => Text(c, "name")));
            if (keyText.Length == 0) keyText = string.Join(", ", cols.OrderBy(c => Number(c, "index_column_id")).Select(c => Text(c, "name")));
            var notes = new List<string> { Text(row, "Description") };
            if (includeText.Length > 0) notes.Add("INCLUDE (" + includeText + ")");
            if (Flag(row, "has_filter")) notes.Add("WHERE " + Text(row, "filter_definition"));
            if (Flag(row, "is_disabled")) notes.Add("DISABLED");
            if (Flag(row, "is_hypothetical")) notes.Add("HYPOTHETICAL");
            obj.Indexes.Add(new IndexDefinition(Text(row, "name"), keyText, Flag(row, "is_primary_key"),
                Flag(row, "is_unique"), JoinNotes(notes)));
            if (Text(row, "constraint_name").Length != 0)
                obj.Constraints.Add(new ConstraintDefinition(Text(row, "constraint_name"),
                    Flag(row, "is_primary_key") ? "PRIMARY KEY" : "UNIQUE", keyText));
        }
        foreach (DataRow row in (await Query(ConstraintsSql)).Rows)
            if (byId.TryGetValue(Number(row, "parent_object_id"), out var obj))
                obj.Constraints.Add(new ConstraintDefinition(Text(row, "name"), Text(row, "kind"),
                    JoinNotes(new[] { Text(row, "definition"), Text(row, "state") })));

        // Read all FK edges so references from objects outside the selected schema are retained.
        foreach (var group in (await Query(ForeignKeysSql)).AsEnumerable().GroupBy(r => Number(r, "object_id")))
        {
            var row = group.First();
            var ordered = group.OrderBy(r => Number(r, "constraint_column_id")).ToArray();
            var fk = new ForeignKeyDefinition(Text(row, "name"),
                Text(row, "ParentSchema") + "." + Text(row, "ParentTable"),
                string.Join(", ", ordered.Select(r => Text(r, "ParentColumn"))),
                Text(row, "ReferencedSchema") + "." + Text(row, "ReferencedTable"),
                string.Join(", ", ordered.Select(r => Text(r, "ReferencedColumn"))),
                JoinNotes(new[] { "ON UPDATE " + Text(row, "update_referential_action_desc"),
                    "ON DELETE " + Text(row, "delete_referential_action_desc"),
                    Flag(row, "is_disabled") ? "DISABLED" : "", Flag(row, "is_not_trusted") ? "NOT TRUSTED" : "" }));
            if (byId.TryGetValue(Number(row, "parent_object_id"), out var parent)) parent.ForeignKeys.Add(fk);
            if (byId.TryGetValue(Number(row, "referenced_object_id"), out var target)) target.ReferencedBy.Add(fk);
        }
        foreach (var group in (await Query(TriggersSql)).AsEnumerable().GroupBy(r => Number(r, "object_id")))
        {
            var row = group.First();
            if (!byId.TryGetValue(Number(row, "parent_id"), out var obj)) continue;
            obj.Triggers.Add(new TriggerDefinition(Text(row, "name"),
                string.Join(", ", group.Select(r => Text(r, "EventName")).Where(s => s.Length > 0).Distinct().Order()),
                Flag(row, "is_instead_of_trigger") ? "INSTEAD OF" : "AFTER",
                JoinNotes(new[] { Text(row, "type_desc"), Flag(row, "is_disabled") ? "DISABLED" : "ENABLED",
                    Flag(row, "is_not_for_replication") ? "NOT FOR REPLICATION" : "" }),
                OptionalText(row, "definition")));
        }
        return model;
    }

    /// <summary>Allows callers to supplement logical names/remarks before rendering. Writes text, never formulas.</summary>
    public void WriteWorkbook(DatabaseDefinition model, string outputPath,
        ExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (model.Objects.Count == 0) throw new ArgumentException("At least one object is required.", nameof(model));
        if (!string.Equals(Path.GetExtension(outputPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must have the .xlsx extension.", nameof(outputPath));
        options ??= new();
        using var workbook = new XLWorkbook();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in model.Objects.OrderBy(o => o.Schema, StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = workbook.AddWorksheet(SheetName(obj, usedNames));
            Render(sheet, obj, model.Rdbms, options, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            workbook.SaveAs(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Render(IXLWorksheet ws, ObjectDefinition obj, string rdbms, ExportOptions options, CancellationToken ct)
    {
        var widths = new[] { 3d, 16d, 14d, 14d, 8d, 8d, 28d };
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.Style.Font.FontName = options.FontName;
        ws.Style.Font.FontSize = 9;
        ws.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Style.Alignment.WrapText = true;
        ws.RowHeight = 13.5;
        ws.ShowGridLines = true;
        var green = XLColor.FromHtml("#92D050");
        void Cell(int row, int first, int last, string value, bool header = false)
        {
            if (value.Length > 32767) throw new InvalidOperationException($"Excel cell limit exceeded: {obj.Schema}.{obj.Name}, row {row}.");
            var range = ws.Range(row, first, row, last);
            if (last != first) range.Merge();
            range.FirstCell().SetValue(value);
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            if (header) { range.Style.Fill.BackgroundColor = green; range.Style.Font.Bold = true; }
            var width = Math.Max(3, widths.Skip(first - 1).Take(last - first + 1).Sum());
            var lines = value.Replace("\r", "").Split('\n').Sum(line =>
                Math.Max(1, (int)Math.Ceiling(line.EnumerateRunes().Sum(r => r.Value > 255 ? 2d : 1d) / width)));
            var height = lines * 13.5;
            if (height > 409)
                throw new InvalidOperationException($"Text exceeds printable row height: {obj.Schema}.{obj.Name}, row {row}. Shorten the remarks or customize the layout.");
            ws.Row(row).Height = Math.Max(ws.Row(row).Height, height);
        }
        void Title(int row, string title)
        { ws.Range(row, 1, row, 7).Merge().FirstCell().SetValue(title); ws.Row(row).Height = 13.5; ws.Cell(row, 1).Style.Font.Bold = true; }
        Title(1, obj.IsView ? "ビュー情報" : "テーブル情報");
        var leftLabels = new[] { "システム名", "サブシステム名", "スキーマ名", "論理テーブル名", "物理テーブル名" };
        var leftValues = new[] { options.SystemName, options.SubsystemName, obj.Schema, obj.LogicalName, obj.Name };
        var rightLabels = new[] { "作成者", "作成日", "更新日", "RDBMS", "" };
        var rightValues = new[] { options.Author, Date(options.CreatedDate), Date(options.UpdatedDate), rdbms, "" };
        for (var i = 0; i < 5; i++)
        {
            Cell(i + 2, 2, 2, leftLabels[i], true); Cell(i + 2, 3, 4, leftValues[i]);
            Cell(i + 2, 5, 5, rightLabels[i], true); Cell(i + 2, 6, 7, rightValues[i]);
        }
        ws.Range("B7:G7").Merge().FirstCell().SetValue("備考");
        Cell(8, 2, 7, obj.Remarks);
        var remarksHeight = ws.Row(8).Height;
        ws.Range("B8:G8").Unmerge();
        ws.Range("B8:G10").Merge();
        ws.Range("B7:G10").Style.Border.InsideBorder = XLBorderStyleValues.None;
        ws.Range("B7:G10").Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range("B2:G10").Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
        ws.Range("B7:G10").Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        for (var r = 8; r <= 10; r++) ws.Row(r).Height = Math.Max(13.5, remarksHeight / 3);
        var rowNumber = 12;
        void Section(string title, string[] headers, int[] spans, IEnumerable<string[]> rows)
        {
            ct.ThrowIfCancellationRequested();
            Title(rowNumber++, title);
            void Row(string[] values, bool header)
            {
                var col = 1;
                for (var i = 0; i < values.Length; i++) { Cell(rowNumber, col, col + spans[i] - 1, values[i], header); col += spans[i]; }
                rowNumber++;
            }
            var firstRow = rowNumber;
            Row(headers, true);
            foreach (var values in rows)
            {
                ct.ThrowIfCancellationRequested();
                Row(values, false);
                ws.Range(rowNumber - 1, 1, rowNumber - 1, 7).Style.Border.TopBorder = XLBorderStyleValues.Dotted;
                ws.Range(rowNumber - 1, 1, rowNumber - 1, 7).Style.Border.BottomBorder = XLBorderStyleValues.Dotted;
                ws.Cell(rowNumber - 1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            }
            ws.Range(firstRow, 1, firstRow, 7).Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            ws.Range(firstRow, 1, rowNumber - 1, 7).Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            rowNumber++;
        }
        Section("カラム情報", new[] { "No.", "論理名", "物理名", "データ型", "Not Null", "デフォルト", "備考" },
            new[] { 1, 1, 1, 1, 1, 1, 1 }, obj.Columns.OrderBy(c => c.Ordinal).Select((c, i) => new[] {
                (i + 1).ToString(CultureInfo.InvariantCulture), c.LogicalName, c.Name, c.DataType,
                (c.NotNull ? "Yes" : "") + (c.PrimaryKey ? " (PK)" : ""), c.DefaultValue, c.Remarks }));
        Section("インデックス情報", new[] { "No.", "インデックス名", "カラムリスト", "主キー", "ユニーク", "備考" },
            new[] { 1, 1, 2, 1, 1, 1 }, obj.Indexes.Select((x, i) => new[] { (i + 1).ToString(), x.Name, x.Columns, Yes(x.PrimaryKey), Yes(x.Unique), x.Remarks }));
        Section("制約情報", new[] { "No.", "制約名", "種類", "制約定義" }, new[] { 1, 1, 1, 4 },
            obj.Constraints.Select((x, i) => new[] { (i + 1).ToString(), x.Name, x.Type, x.Definition }));
        Section("外部キー情報", new[] { "No.", "外部キー名", "カラムリスト", "参照先テーブル名", "参照先カラムリスト" },
            new[] { 1, 1, 2, 2, 1 }, obj.ForeignKeys.Select((x, i) => new[] { (i + 1).ToString(), x.Name, x.ParentColumns, x.Referenced, x.ReferencedColumns }));
        Section("外部キー情報(PK側)", new[] { "No.", "外部キー名", "カラムリスト", "参照元テーブル名", "参照元カラムリスト" },
            new[] { 1, 1, 2, 2, 1 }, obj.ReferencedBy.Select((x, i) => new[] { (i + 1).ToString(), x.Name, x.ReferencedColumns, x.Parent, x.ParentColumns }));
        Section("トリガー情報", new[] { "No.", "トリガー名", "イベント", "タイミング", "条件" }, new[] { 1, 1, 2, 2, 1 },
            obj.Triggers.Select((x, i) => new[] { (i + 1).ToString(), x.Name,
                string.Join(",", x.Events.Split(',').Select(e => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(e.Trim().ToLowerInvariant()))),
                x.Timing.Equals("AFTER", StringComparison.OrdinalIgnoreCase) ? "After statement" :
                    x.Timing.Equals("INSTEAD OF", StringComparison.OrdinalIgnoreCase) ? "Instead of statement" : x.Timing, "" }));
        void Definition(string title, string? sql)
        {
            Title(rowNumber++, title);
            // Split long SQL lines into bounded chunks; never silently truncate definitions.
            foreach (var line in (sql ?? "定義取得不可（暗号化、CLR または権限制限）").Replace("\r", "").Split('\n'))
            {
                for (var offset = 0; offset < Math.Max(1, line.Length); offset += 140)
                    Cell(rowNumber++, 1, 7, line.Substring(offset, Math.Min(140, line.Length - offset)));
            }
            rowNumber++;
        }
        if (options.IncludeSqlDefinitions)
        {
            if (obj.IsView) Definition("ビュー定義 SQL", obj.SqlDefinition);
            foreach (var trigger in obj.Triggers) Definition("トリガー定義 SQL: " + trigger.Name, trigger.SqlDefinition);
        }
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.Left = 0.25;
        ws.PageSetup.Margins.Right = 0.25;
        ws.PageSetup.PrintAreas.Add(1, 1, rowNumber - 1, 7);
    }

    private static string SheetName(ObjectDefinition obj, HashSet<string> used)
    {
        var raw = string.IsNullOrWhiteSpace(obj.LogicalName) ? obj.Schema + "." + obj.Name : obj.LogicalName;
        var name = new string(raw.Select(c => "[]:*?/\\".Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('\'');
        if (name.Length == 0) name = "Table";
        if (name.Equals("History", StringComparison.OrdinalIgnoreCase)) name = "History_";
        var basis = name;
        for (var n = 1; ; n++)
        {
            var suffix = n == 1 ? "" : "_" + n;
            name = basis[..Math.Min(basis.Length, 31 - suffix.Length)] + suffix;
            name = name.TrimEnd('\'');
            if (used.Add(name)) return name;
        }
    }
    private static string Date(DateOnly? date) => date?.ToString("yyyy/M/d", CultureInfo.InvariantCulture) ?? "";
    private static string Yes(bool value) => value ? "Yes" : "";
    private static string Text(DataRow row, string name) => Convert.ToString(row[name], CultureInfo.InvariantCulture) ?? "";
    private static string? OptionalText(DataRow row, string name) => row.IsNull(name) ? null : Text(row, name);
    private static int Number(DataRow row, string name) => row.IsNull(name) ? 0 : Convert.ToInt32(row[name], CultureInfo.InvariantCulture);
    private static bool Flag(DataRow row, string name) => Number(row, name) != 0;
    private static string JoinNotes(IEnumerable<string> notes) => string.Join("\n", notes.Where(n => !string.IsNullOrWhiteSpace(n)));
    private static string FormatType(DataRow row)
    {
        var name = Text(row, "TypeName");
        if (Flag(row, "is_user_defined")) return Text(row, "TypeSchema") + "." + name;
        var length = Number(row, "max_length");
        return name.ToLowerInvariant() switch
        {
            "nvarchar" or "nchar" => name + "(" + (length == -1 ? "max" : (length / 2).ToString(CultureInfo.InvariantCulture)) + ")",
            "varchar" or "char" or "varbinary" or "binary" => name + "(" + (length == -1 ? "max" : length.ToString(CultureInfo.InvariantCulture)) + ")",
            "decimal" or "numeric" => $"{name}({Number(row, "precision")},{Number(row, "scale")})",
            "datetime2" or "datetimeoffset" or "time" => $"{name}({Number(row, "scale")})",
            "float" => $"{name}({Number(row, "precision")})",
            _ => name
        };
    }

    private const string ObjectsSql = """
        SELECT o.object_id, s.name AS SchemaName, o.name AS ObjectName, RTRIM(o.type) AS ObjectType,
          CONVERT(nvarchar(max), COALESCE(lp.value, dp.value)) AS LogicalName,
          CONVERT(nvarchar(max), rp.value) AS Remarks, m.definition
        FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
        LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
        LEFT JOIN sys.extended_properties dp ON dp.class=1 AND dp.major_id=o.object_id AND dp.minor_id=0 AND dp.name=@description
        LEFT JOIN sys.extended_properties lp ON lp.class=1 AND lp.major_id=o.object_id AND lp.minor_id=0 AND lp.name=@logical
        LEFT JOIN sys.extended_properties rp ON rp.class=1 AND rp.major_id=o.object_id AND rp.minor_id=0 AND rp.name=@remarks
        WHERE o.type IN ('U','V') AND o.is_ms_shipped=0 ORDER BY s.name, o.name;
        """;
    private const string ColumnsSql = """
        SELECT c.object_id, c.column_id, c.name, t.name AS TypeName, ts.name AS TypeSchema,
          t.is_user_defined, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
          c.is_computed, c.is_rowguidcol, c.is_sparse, c.is_hidden, c.generated_always_type_desc, c.collation_name,
          CONVERT(nvarchar(128), ic.seed_value) AS seed_value, CONVERT(nvarchar(128), ic.increment_value) AS increment_value,
          cc.definition AS computed_definition, cc.is_persisted, OBJECT_DEFINITION(c.default_object_id) AS default_definition,
          CONVERT(nvarchar(max), COALESCE(lp.value, dp.value)) AS LogicalName,
          CONVERT(nvarchar(max), rp.value) AS Remarks,
          CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ix JOIN sys.indexes i
            ON i.object_id=ix.object_id AND i.index_id=ix.index_id
            WHERE ix.object_id=c.object_id AND ix.column_id=c.column_id AND i.is_primary_key=1) THEN 1 ELSE 0 END AS IsPk
        FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id
        JOIN sys.types t ON t.user_type_id=c.user_type_id JOIN sys.schemas ts ON ts.schema_id=t.schema_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
        LEFT JOIN sys.extended_properties dp ON dp.class=1 AND dp.major_id=c.object_id AND dp.minor_id=c.column_id AND dp.name=@description
        LEFT JOIN sys.extended_properties lp ON lp.class=1 AND lp.major_id=c.object_id AND lp.minor_id=c.column_id AND lp.name=@logical
        LEFT JOIN sys.extended_properties rp ON rp.class=1 AND rp.major_id=c.object_id AND rp.minor_id=c.column_id AND rp.name=@remarks
        WHERE o.type IN ('U','V') AND o.is_ms_shipped=0 ORDER BY c.object_id, c.column_id;
        """;
    private const string IndexColumnsSql = """
        SELECT ic.object_id, ic.index_id, ic.index_column_id, ic.key_ordinal, ic.is_descending_key,
          ic.is_included_column, c.name
        FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
        JOIN sys.objects o ON o.object_id=ic.object_id WHERE o.type IN ('U','V') AND o.is_ms_shipped=0;
        """;
    private const string IndexesSql = """
        SELECT i.object_id, i.index_id, i.name, i.type_desc, i.is_primary_key, i.is_unique,
          i.has_filter, i.filter_definition, i.is_disabled, i.is_hypothetical, i.fill_factor,
          ds.name AS data_space, k.name AS constraint_name, CONVERT(nvarchar(max), ep.value) AS Description
        FROM sys.indexes i JOIN sys.objects o ON o.object_id=i.object_id
        LEFT JOIN sys.data_spaces ds ON ds.data_space_id=i.data_space_id
        LEFT JOIN sys.key_constraints k ON k.parent_object_id=i.object_id AND k.unique_index_id=i.index_id
        LEFT JOIN sys.extended_properties ep ON ep.class=7 AND ep.major_id=i.object_id AND ep.minor_id=i.index_id AND ep.name=@description
        WHERE o.type IN ('U','V') AND o.is_ms_shipped=0 AND i.index_id>0 ORDER BY i.object_id, i.index_id;
        """;
    private const string ConstraintsSql = """
        SELECT parent_object_id, name, 'CHECK' AS kind, definition,
          CASE WHEN is_disabled=1 THEN 'DISABLED ' ELSE '' END + CASE WHEN is_not_trusted=1 THEN 'NOT TRUSTED' ELSE '' END AS state
        FROM sys.check_constraints
        UNION ALL
        SELECT d.parent_object_id, d.name, 'DEFAULT', c.name+' = '+d.definition, ''
        FROM sys.default_constraints d JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
        ORDER BY parent_object_id, name;
        """;
    private const string ForeignKeysSql = """
        SELECT f.object_id, f.name, f.parent_object_id, f.referenced_object_id,
          ps.name AS ParentSchema, p.name AS ParentTable, pc.name AS ParentColumn,
          rs.name AS ReferencedSchema, r.name AS ReferencedTable, rc.name AS ReferencedColumn,
          fc.constraint_column_id, f.update_referential_action_desc, f.delete_referential_action_desc, f.is_disabled, f.is_not_trusted
        FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=f.object_id
        JOIN sys.tables p ON p.object_id=f.parent_object_id JOIN sys.schemas ps ON ps.schema_id=p.schema_id
        JOIN sys.tables r ON r.object_id=f.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=r.schema_id
        JOIN sys.columns pc ON pc.object_id=p.object_id AND pc.column_id=fc.parent_column_id
        JOIN sys.columns rc ON rc.object_id=r.object_id AND rc.column_id=fc.referenced_column_id
        ORDER BY f.object_id, fc.constraint_column_id;
        """;
    private const string TriggersSql = """
        SELECT t.object_id, t.parent_id, t.name, t.type_desc, t.is_disabled, t.is_instead_of_trigger,
          t.is_not_for_replication, e.type_desc AS EventName, m.definition
        FROM sys.triggers t LEFT JOIN sys.trigger_events e ON e.object_id=t.object_id
        LEFT JOIN sys.sql_modules m ON m.object_id=t.object_id
        WHERE t.parent_class=1 AND t.is_ms_shipped=0 ORDER BY t.parent_id, t.name, e.type_desc;
        """;
}


