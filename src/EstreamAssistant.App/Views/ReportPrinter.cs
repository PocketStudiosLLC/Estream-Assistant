using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using EstreamAssistant.Core.Reports;

namespace EstreamAssistant.App.Views;

/// <summary>Prints a report as a plain black-on-white table (Windows print dialog first).</summary>
public static class ReportPrinter
{
    public static void Print(Window owner, Report r)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.5,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            PageWidth = dlg.PrintableAreaWidth,
            PageHeight = dlg.PrintableAreaHeight,
            PagePadding = new Thickness(40),
        };
        doc.ColumnWidth = doc.PageWidth; // one column of text, not two

        doc.Blocks.Add(new Paragraph(new Run(r.Title)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        doc.Blocks.Add(new Paragraph(new Run($"{r.Summary} · printed {DateTime.Now:MMM d, yyyy h:mm tt}"))
            { FontSize = 10, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var note in r.Notes)
            doc.Blocks.Add(new Paragraph(new Run("• " + note)) { Margin = new Thickness(0, 0, 0, 4) });

        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var c in r.Columns)
            table.Columns.Add(new TableColumn { Width = new GridLength(c.Header == "Description" || c.Header == "Comment" ? 3 : 1, GridUnitType.Star) });

        var header = new TableRowGroup();
        var hr = new TableRow { Background = Brushes.Gainsboro };
        foreach (var c in r.Columns) hr.Cells.Add(Cell(c.Header, c.Kind, bold: true));
        header.Rows.Add(hr);
        table.RowGroups.Add(header);

        var body = new TableRowGroup();
        int n = 0;
        foreach (var row in r.Rows)
        {
            var tr = new TableRow { Background = n++ % 2 == 1 ? new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)) : Brushes.White };
            for (int i = 0; i < r.Columns.Count; i++)
                tr.Cells.Add(Cell(Report.Display(i < row.Length ? row[i] : null, r.Columns[i].Kind), r.Columns[i].Kind, bold: false));
            body.Rows.Add(tr);
        }
        table.RowGroups.Add(body);
        doc.Blocks.Add(table);

        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, r.Title);
    }

    private static TableCell Cell(string text, ReportColumnKind kind, bool bold) => new(new Paragraph(new Run(text))
    {
        Margin = new Thickness(0),
        TextAlignment = kind is ReportColumnKind.Number or ReportColumnKind.Money ? TextAlignment.Right : TextAlignment.Left,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    })
    {
        Padding = new Thickness(4, 2, 4, 2),
        BorderBrush = Brushes.LightGray,
        BorderThickness = new Thickness(0, 0, 0, 0.5),
    };
}
