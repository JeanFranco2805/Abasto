using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SupermercadoPOS.Domain;

namespace SupermercadoPOS.Services;

public static class ReceiptPrintService
{
    public static bool PrintSale(Window owner, Sale sale)
    {
        var dialog = new PrintDialog { UserPageRangeEnabled = false };
        if (dialog.ShowDialog() != true)
            return false;

        var document = BuildDocument(sale);
        dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, $"Ticket POS #{sale.Id}");
        return true;
    }

    private static FlowDocument BuildDocument(Sale sale)
    {
        var document = new FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 10,
            PagePadding = new Thickness(10),
            PageWidth = 280,
            PageHeight = Math.Max(480, 220 + sale.Items.Count * 54 + sale.Payments.Count * 24),
            ColumnWidth = 280,
            ColumnGap = 0
        };
        document.Blocks.Add(MakeParagraph("SUPERMERCADO", true, TextAlignment.Center));
        document.Blocks.Add(MakeParagraph("COMPROBANTE DE VENTA", false, TextAlignment.Center));
        document.Blocks.Add(MakeParagraph($"Venta #{sale.Id}  {sale.CreatedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm}", false));
        document.Blocks.Add(MakeParagraph($"Cajero: {sale.CashierName}", false));
        if (!string.IsNullOrWhiteSpace(sale.CustomerName))
            document.Blocks.Add(MakeParagraph($"Cliente: {sale.CustomerName}", false));
        if (!string.IsNullOrWhiteSpace(sale.CustomerDocument))
            document.Blocks.Add(MakeParagraph($"Documento: {sale.CustomerDocument}", false));
        document.Blocks.Add(MakeParagraph(new string('-', 32), false));

        foreach (var item in sale.Items)
        {
            document.Blocks.Add(MakeParagraph(item.ProductName, true));
            document.Blocks.Add(MakeParagraph(
                $"{item.Quantity:0.###} {item.Unit} x {item.UnitPrice:C0} = {item.LineTotal:C0}", false));
        }

        document.Blocks.Add(MakeParagraph(new string('-', 32), false));
        document.Blocks.Add(MakeParagraph($"Subtotal: {sale.Subtotal:C0}", false));
        if (sale.DiscountTotal > 0)
            document.Blocks.Add(MakeParagraph($"Descuentos: -{sale.DiscountTotal:C0}", false));
        document.Blocks.Add(MakeParagraph($"Impuestos: {sale.TaxTotal:C0}", false));
        document.Blocks.Add(MakeParagraph($"TOTAL: {sale.Total:C0}", true));
        foreach (var payment in sale.Payments)
            document.Blocks.Add(MakeParagraph($"{payment.Method}: {payment.Amount:C0}", false));
        var change = sale.Payments.Sum(p => p.Change);
        if (change > 0)
            document.Blocks.Add(MakeParagraph($"Cambio: {change:C0}", true));
        document.Blocks.Add(MakeParagraph("Gracias por su compra", false, TextAlignment.Center));
        return document;
    }

    private static Paragraph MakeParagraph(string text, bool bold, TextAlignment alignment = TextAlignment.Left)
    {
        var paragraph = new Paragraph { TextAlignment = alignment, Margin = new Thickness(0, 2, 0, 2) };
        if (bold)
            paragraph.Inlines.Add(new Bold(new Run(text)));
        else
            paragraph.Inlines.Add(new Run(text));
        return paragraph;
    }
}
