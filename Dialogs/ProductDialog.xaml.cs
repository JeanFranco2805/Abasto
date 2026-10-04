using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SupermercadoPOS.Domain;

namespace SupermercadoPOS.Dialogs;

public partial class ProductDialog : Window
{
    private readonly int _id;
    public Product? ResultProduct { get; private set; }

    public ProductDialog(Product? product = null)
    {
        InitializeComponent();
        _id = product?.Id ?? 0;
        TitleText.Text = product is null ? "Nuevo producto" : "Editar producto";
        if (product is null)
            return;

        BarcodeBox.Text = product.Barcode;
        NameBox.Text = product.Name;
        CategoryBox.Text = product.Category;
        UnitBox.SelectedIndex = product.Unit == "KG" ? 1 : 0;
        PriceBox.Text = product.UnitPrice.ToString("0.00", CultureInfo.CurrentCulture);
        TaxBox.Text = (product.TaxRate * 100m).ToString("0.##", CultureInfo.CurrentCulture);
        StockBox.Text = product.Stock.ToString("0.###", CultureInfo.CurrentCulture);
        MinimumStockBox.Text = product.MinimumStock.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (!TryParse(PriceBox.Text, out var price)
            || !TryParse(TaxBox.Text, out var taxPercent)
            || !TryParse(StockBox.Text, out var stock)
            || !TryParse(MinimumStockBox.Text, out var minimumStock))
        {
            ErrorText.Text = "Revisa los valores de precio, IVA e inventario.";
            return;
        }
        if (string.IsNullOrWhiteSpace(BarcodeBox.Text) || string.IsNullOrWhiteSpace(NameBox.Text)
            || price < 0 || taxPercent is < 0 or > 100 || stock < 0 || minimumStock < 0)
        {
            ErrorText.Text = "Completa código y nombre; los valores numéricos deben ser válidos.";
            return;
        }

        ResultProduct = new Product
        {
            Id = _id,
            Barcode = BarcodeBox.Text.Trim(),
            Name = NameBox.Text.Trim(),
            Category = CategoryBox.Text.Trim(),
            Unit = (UnitBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UND",
            UnitPrice = price,
            TaxRate = taxPercent / 100m,
            Stock = stock,
            MinimumStock = minimumStock,
            IsActive = true
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryParse(string value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out result)
        || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
}
