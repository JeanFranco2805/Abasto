using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Abasto.Domain;
using Abasto.Services;

namespace Abasto.Dialogs;

public partial class ProductDialog : Window
{
    private readonly int _id;
    private readonly string _initialUnit;
    private readonly string _existingImagePath;
    private string? _selectedImageSource;
    private bool _removeImage;

    public Product? ResultProduct { get; private set; }

    public ProductDialog(Product? product = null)
    {
        InitializeComponent();
        _id = product?.Id ?? 0;
        _initialUnit = product?.Unit ?? "UND";
        _existingImagePath = product?.ImagePath ?? "";
        TitleText.Text = product is null ? "Nuevo producto" : "Editar producto";
        if (product is not null)
        {
            BarcodeBox.Text = product.Barcode;
            NameBox.Text = product.Name;
            CategoryBox.Text = product.Category;
            PriceBox.Text = product.UnitPrice.ToString("0.00", CultureInfo.CurrentCulture);
            TaxBox.Text = (product.TaxRate * 100m).ToString("0.##", CultureInfo.CurrentCulture);
            StockBox.Text = product.Stock.ToString("0.###", CultureInfo.CurrentCulture);
            MinimumStockBox.Text = product.MinimumStock.ToString("0.###", CultureInfo.CurrentCulture);
        }

        Loaded += async (_, _) => await LoadUnitsAndImageAsync();
    }

    private async Task LoadUnitsAndImageAsync()
    {
        try
        {
            var units = await UnitCatalogService.GetUnitsAsync();
            if (!units.Contains(_initialUnit, StringComparer.OrdinalIgnoreCase))
                units.Add(_initialUnit);
            UnitBox.ItemsSource = units;
            UnitBox.SelectedItem = units.FirstOrDefault(unit =>
                string.Equals(unit, _initialUnit, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"No se pudieron cargar las unidades: {ex.Message}";
        }

        if (!string.IsNullOrWhiteSpace(_existingImagePath) && File.Exists(_existingImagePath))
        {
            try
            {
                ShowImage(_existingImagePath);
            }
            catch (Exception ex)
            {
                ImageNameText.Text = $"No se pudo abrir la imagen: {ex.Message}";
            }
        }
    }

    private async void ManageUnits_Click(object sender, RoutedEventArgs e)
    {
        var selectedUnit = UnitBox.SelectedItem?.ToString() ?? _initialUnit;
        var dialog = new UnitManagerDialog(selectedUnit) { Owner = this };
        dialog.ShowDialog();

        try
        {
            var units = await UnitCatalogService.GetUnitsAsync();
            if (!units.Contains(selectedUnit, StringComparer.OrdinalIgnoreCase))
                units.Add(selectedUnit);
            UnitBox.ItemsSource = units;
            UnitBox.SelectedItem = units.FirstOrDefault(unit =>
                string.Equals(unit, selectedUnit, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"No se pudieron cargar las unidades: {ex.Message}";
        }
    }

    private void SelectImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar imagen del producto",
            Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.bmp|JPEG|*.jpg;*.jpeg|PNG|*.png|BMP|*.bmp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var file = new FileInfo(dialog.FileName);
        if (file.Length > 10 * 1024 * 1024)
        {
            ErrorText.Text = "La imagen supera el límite de 10 MB.";
            return;
        }

        try
        {
            ShowImage(dialog.FileName);
            _selectedImageSource = dialog.FileName;
            _removeImage = false;
            ErrorText.Text = "";
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"No se pudo abrir la imagen: {ex.Message}";
        }
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        _selectedImageSource = null;
        _removeImage = true;
        ImagePreview.Source = null;
        EmptyImageHint.Visibility = Visibility.Visible;
        ImageNameText.Text = "La imagen se quitará al guardar";
    }

    private void ShowImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 512;
        image.UriSource = new Uri(Path.GetFullPath(path));
        image.EndInit();
        image.Freeze();

        ImagePreview.Source = image;
        EmptyImageHint.Visibility = Visibility.Collapsed;
        ImageNameText.Text = Path.GetFileName(path);
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

        var unit = UnitBox.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(unit))
        {
            ErrorText.Text = "Selecciona o registra una unidad de medida.";
            return;
        }

        string imagePath;
        try
        {
            imagePath = _removeImage
                ? ""
                : _selectedImageSource is null
                    ? _existingImagePath
                    : CopyImageToStorage(_selectedImageSource);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"No se pudo guardar la imagen: {ex.Message}";
            return;
        }

        ResultProduct = new Product
        {
            Id = _id,
            Barcode = BarcodeBox.Text.Trim(),
            Name = NameBox.Text.Trim(),
            Category = CategoryBox.Text.Trim(),
            Unit = unit,
            ImagePath = imagePath,
            UnitPrice = price,
            TaxRate = taxPercent / 100m,
            Stock = stock,
            MinimumStock = minimumStock,
            IsActive = true
        };
        DialogResult = true;
    }

    private static string CopyImageToStorage(string sourcePath)
    {
        var dataDirectory = Path.GetDirectoryName(Abasto.App.DatabasePath)
            ?? throw new InvalidOperationException("No se encontró la carpeta de datos del punto de venta.");
        var imageDirectory = Path.Combine(dataDirectory, "product-images");
        Directory.CreateDirectory(imageDirectory);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var destination = Path.Combine(imageDirectory, $"{Guid.NewGuid():N}{extension}");
        File.Copy(sourcePath, destination);
        return destination;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool TryParse(string value, out decimal result) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out result)
        || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
}
