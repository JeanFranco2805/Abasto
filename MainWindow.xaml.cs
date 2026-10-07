using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Abasto.Dialogs;
using Abasto.Domain;
using Abasto.Services;

namespace Abasto;

public partial class MainWindow : Window
{
    private readonly List<Product> _allProducts = [];
    private List<Product> _filteredInventoryProducts = [];
    private int _inventoryCurrentPage = 1;
    private bool _suppressInventoryFilterEvents;
    private CashShift? _openShift;
    private long? _currentSuspendedSaleId;
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private readonly SerialScaleService _serialScale = new();

    public ObservableCollection<Product> DisplayProducts { get; } = [];
    public ObservableCollection<InventoryProductCard> InventoryProducts { get; } = [];
    public ObservableCollection<string> InventoryCategories { get; } = ["Todas las categorías"];
    public ObservableCollection<CartLineViewModel> Cart { get; } = [];
    public MainWindow()
    {
        InitializeComponent();
        MainTabs.SelectedItem = SalesTab;
        DataContext = this;
        UserNameText.Text = Session.CurrentUser?.DisplayName ?? "Usuario";
        UserRoleText.Text = Session.CurrentUser?.Role ?? "";
        UsersTab.Visibility = Session.CurrentUser?.Role == "Administrador" ? Visibility.Visible : Visibility.Collapsed;
        _syncTimer.Tick += async (_, _) => await SynchronizeAndRefreshAsync();
        Closed += (_, _) => _syncTimer.Stop();
        Loaded += async (_, _) =>
        {
            ReportFromDate.SelectedDate = DateTime.Today;
            ReportToDate.SelectedDate = DateTime.Today;
            await ReloadProductsAsync();
            await RefreshShiftAsync();
            RefreshTotals();
            await RefreshShiftPageAsync();
            MainTabs.SelectedItem = SalesTab;
            await LoadBackendSettingsAsync();
            await SynchronizeAndRefreshAsync();
            _syncTimer.Start();
        };
        Cart.CollectionChanged += (_, _) => RefreshTotals();
    }

    private async Task ReloadProductsAsync()
    {
        var localProducts = await PosService.GetProductsAsync();
        DisplayProducts.Clear();
        _allProducts.Clear();
        _allProducts.AddRange(localProducts);
        ApplyProductFilters();
        RefreshInventoryCategories();
        ApplyInventoryFilter();

        var serverProducts = await BackendSyncService.GetServerProductsAsync();
        InventoryDataSourceText.Text = serverProducts is null
            ? "Fuente: copia local"
            : "Fuente: servidor central";
        if (serverProducts is null)
            return;

        var products = await PosService.MergeServerProductsAsync(serverProducts);
        _allProducts.Clear();
        _allProducts.AddRange(products);
        ApplyProductFilters();
        RefreshInventoryCategories();
        ApplyInventoryFilter();
    }

    private void ApplyProductFilters()
    {
        var term = SearchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(term)
            ? _allProducts
            : _allProducts.Where(p => p.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || p.Barcode.Contains(term, StringComparison.OrdinalIgnoreCase)
                || p.Category.Contains(term, StringComparison.CurrentCultureIgnoreCase)).ToList();

        DisplayProducts.Clear();
        foreach (var product in filtered.Take(100))
            DisplayProducts.Add(product);
        ProductsPanel.ItemsSource = DisplayProducts;
        ProductCountText.Text = $"{filtered.Count} producto(s)";
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ProductsPanel is not null && ProductCountText is not null)
            ApplyProductFilters();
    }

    private void Product_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is Product product)
            AddToCart(product);
    }

    private async void BarcodeAdd_Click(object sender, RoutedEventArgs e) => await AddBarcodeAsync();

    private async void Barcode_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await AddBarcodeAsync();
    }

    private async Task AddBarcodeAsync()
    {
        var barcode = BarcodeBox.Text.Trim();
        if (barcode.Length == 0)
            return;
        var product = _allProducts.FirstOrDefault(p => p.Barcode.Equals(barcode, StringComparison.OrdinalIgnoreCase));
        product ??= await PosService.FindProductByBarcodeAsync(barcode);
        if (product is null)
        {
            MessageBox.Show($"No se encontró el código {barcode}.", "Producto no encontrado", MessageBoxButton.OK, MessageBoxImage.Information);
            BarcodeBox.SelectAll();
            BarcodeBox.Focus();
            return;
        }

        AddToCart(product);
        BarcodeBox.Clear();
        BarcodeBox.Focus();
    }

    private async void ReadScale_Click(object sender, RoutedEventArgs e)
    {
        if (CartList.SelectedItem is not CartLineViewModel line
            || !(line.Unit.Equals("KG", StringComparison.OrdinalIgnoreCase)
                || line.Unit.Equals("G", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Selecciona un producto vendido por peso en kg o g.", "Báscula",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var kilograms = await _serialScale.ReadWeightAsync();
            var quantity = line.Unit.Equals("G", StringComparison.OrdinalIgnoreCase) ? kilograms * 1000m : kilograms;
            var product = _allProducts.FirstOrDefault(item => item.Id == line.ProductId);
            if (product is not null && quantity > product.Stock)
                throw new InvalidOperationException($"La báscula marcó {quantity:N3} {line.Unit}, pero solo hay {product.Stock:N3} {line.Unit} disponibles.");
            line.Quantity = quantity;
            RefreshTotals();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "No se pudo leer la báscula", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddToCart(Product product)
    {
        var quantityStep = ProductUnitRules.AllowsFractionalQuantity(product.Unit) ? 0.1m : 1m;
        var existing = Cart.FirstOrDefault(c => c.ProductId == product.Id);
        var nextQuantity = (existing?.Quantity ?? 0m) + quantityStep;
        if (nextQuantity > product.Stock)
        {
            MessageBox.Show($"No hay existencias suficientes de {product.Name}. Disponibles: {product.Stock:N3} {product.Unit}.",
                "Inventario insuficiente", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (existing is null)
        {
            var line = new CartLineViewModel(product, quantityStep);
            line.PropertyChanged += (_, _) => RefreshTotals();
            Cart.Add(line);
        }
        else
            existing.Quantity = nextQuantity;
    }

    private void Increase_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CartLineViewModel line)
            return;
        var product = _allProducts.FirstOrDefault(p => p.Id == line.ProductId);
        var step = line.IsWeighted ? 0.1m : 1m;
        if (product is null || line.Quantity + step > product.Stock)
        {
            MessageBox.Show("No hay más existencias disponibles para este producto.", "Inventario insuficiente",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        line.Quantity += step;
    }

    private void Decrease_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CartLineViewModel line)
            return;
        var step = line.IsWeighted ? 0.1m : 1m;
        if (line.Quantity <= step)
            Cart.Remove(line);
        else
            line.Quantity -= step;
    }

    private async void RemoveLine_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CartLineViewModel line)
            return;
        var approver = await RequestSupervisorAsync("Retirar un producto de la venta requiere aprobación.");
        if (approver is not null)
            Cart.Remove(line);
    }

    private async void ClearCart_Click(object sender, RoutedEventArgs e)
    {
        if (Cart.Count == 0)
            return;
        var approver = await RequestSupervisorAsync("Vaciar la venta actual requiere aprobación.");
        if (approver is null)
            return;
        Cart.Clear();
        _currentSuspendedSaleId = null;
    }

    private void RefreshTotals()
    {
        var subtotal = Cart.Sum(line => line.LineSubtotal);
        var discount = Cart.Sum(line => line.DiscountAmount);
        var tax = Cart.Sum(line => line.LineTax);
        var total = subtotal - discount + tax;
        SubtotalText.Text = subtotal.ToString("C0");
        DiscountText.Text = discount.ToString("C0");
        TaxText.Text = tax.ToString("C0");
        TotalText.Text = total.ToString("C0");
        if (Cart.Count == 0)
            CartCountText.Text = "Venta vacía · lista para empezar";
        else
        {
            var quantity = Cart.Sum(line => line.Quantity);
            var productLabel = Cart.Count == 1 ? "producto" : "productos";
            CartCountText.Text = $"{quantity:0.###} unidades · {Cart.Count} {productLabel}";
        }
    }

    private async void Charge_Click(object sender, RoutedEventArgs e)
    {
        if (Cart.Count == 0)
        {
            MessageBox.Show("Agrega al menos un producto a la venta.", "Caja", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_openShift is null)
        {
            MessageBox.Show("Abre un turno de caja antes de cobrar.", "Caja", MessageBoxButton.OK, MessageBoxImage.Information);
            await HandleShiftAsync();
            return;
        }

        var total = Cart.Sum(line => line.LineTotal);
        var paymentDialog = new PaymentDialog(total) { Owner = this };
        if (paymentDialog.ShowDialog() != true)
            return;

        try
        {
            var saleId = await PosService.CompleteSaleAsync(
                Cart.Select(ToDraft).ToList(),
                paymentDialog.Payments,
                _currentSuspendedSaleId,
                CustomerNameBox.Text,
                CustomerDocumentBox.Text);
            if (paymentDialog.Payments.Any(payment => payment.Method == "Efectivo"))
            {
                try
                {
                    var peripheralSettings = await PeripheralSettingsService.LoadAsync();
                    if (!string.IsNullOrWhiteSpace(peripheralSettings.PrinterName))
                        await new WindowsEscPosPeripheralService().OpenAsync();
                }
                catch (Exception drawerError)
                {
                    MessageBox.Show(drawerError.Message, "No se pudo abrir el cajón", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            Cart.Clear();
            _currentSuspendedSaleId = null;
            CustomerNameBox.Clear();
            CustomerDocumentBox.Clear();
            await ReloadProductsAsync();
            await RefreshShiftAsync();
            if (MessageBox.Show($"Venta #{saleId} registrada correctamente.\n\n¿Deseas imprimir el comprobante?",
                    "Venta completada", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                var completedSale = await PosService.GetSaleAsync(saleId);
                if (completedSale is not null)
                {
                    try
                    {
                        var peripheralSettings = await PeripheralSettingsService.LoadAsync();
                        if (string.IsNullOrWhiteSpace(peripheralSettings.PrinterName))
                            ReceiptPrintService.PrintSale(this, completedSale);
                        else
                            await new WindowsEscPosPeripheralService().PrintSaleAsync(completedSale);
                    }
                    catch (Exception printError)
                    {
                        MessageBox.Show(printError.Message, "No se pudo imprimir el comprobante", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            await LoadReportsAsync();
            await RefreshAuditAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo completar la venta", MessageBoxButton.OK, MessageBoxImage.Warning);
            await ReloadProductsAsync();
        }
    }

    private async void Discount_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CartLineViewModel line)
            return;
        var dialog = new DiscountDialog(line.DiscountRate * 100m) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        line.DiscountRate = dialog.DiscountRate;
        line.DiscountApprovedBy = dialog.ApprovedBy;
    }

    private async void SuspendSale_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sale = await PosService.SuspendSaleAsync(Cart.Select(ToDraft).ToList(), _currentSuspendedSaleId);
            _currentSuspendedSaleId = null;
            Cart.Clear();
            MessageBox.Show($"La venta #{sale.Id} quedó suspendida.", "Venta suspendida",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo suspender la venta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ResumeSale_Click(object sender, RoutedEventArgs e)
    {
        if (Cart.Count > 0)
        {
            MessageBox.Show("Cobra, suspende o vacía la venta actual antes de reanudar otra.",
                "Venta en curso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SuspendedSaleDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedSale is null)
            return;
        _currentSuspendedSaleId = dialog.SelectedSale.Id;
        foreach (var item in dialog.SelectedSale.Items)
        {
            var line = new CartLineViewModel(item);
            line.PropertyChanged += (_, _) => RefreshTotals();
            Cart.Add(line);
        }
        RefreshTotals();
    }

    private static SaleLineDraft ToDraft(CartLineViewModel line) =>
        new(line.ProductId, line.Name, line.Barcode, line.Unit, line.Quantity, line.UnitPrice, line.TaxRate,
            line.DiscountRate, line.DiscountApprovedBy);

    private async Task<PosUser?> RequestSupervisorAsync(string message)
    {
        var dialog = new SupervisorApprovalDialog(message) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.ApprovedUser : null;
    }

    private async void Shift_Click(object sender, RoutedEventArgs e) => await HandleShiftAsync();

    private async Task HandleShiftAsync()
    {
        try
        {
            await RefreshShiftAsync();
            var expected = _openShift is null ? 0m : await PosService.CalculateExpectedCashAsync(_openShift.Id);
            var dialog = new ShiftDialog(_openShift is not null, expected) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            switch (dialog.SelectedAction)
            {
                case ShiftAction.Open:
                    await PosService.OpenShiftAsync(dialog.Amount);
                    break;
                case ShiftAction.CashIn:
                    await PosService.RegisterCashMovementAsync(true, dialog.Amount, dialog.Reason);
                    break;
                case ShiftAction.CashOut:
                    await PosService.RegisterCashMovementAsync(false, dialog.Amount, dialog.Reason);
                    break;
                case ShiftAction.Close:
                    if (dialog.Amount != expected
                        && MessageBox.Show($"El efectivo contado difiere del esperado en {(dialog.Amount - expected):C0}. ¿Cerrar el turno de todas formas?",
                            "Diferencia de arqueo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                    await PosService.CloseShiftAsync(_openShift!.Id, dialog.Amount);
                    break;
            }
            await RefreshShiftAsync();
            await RefreshShiftPageAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Turno de caja", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshShiftAsync()
    {
        var userId = Session.CurrentUser?.Id ?? 0;
        _openShift = await PosService.GetOpenShiftAsync(userId);
        ShiftButton.Content = _openShift is null ? "Abrir turno" : "Turno abierto";
        ShiftButton.ToolTip = _openShift is null
            ? "Abrir un nuevo turno"
            : $"Abierto {_openShift.OpenedAtUtc.ToLocalTime():HH:mm} · fondo {_openShift.OpeningFloat:C0}";
        ChargeButtonState();
    }

    private void ChargeButtonState()
    {
        ChargeButton.IsEnabled = _openShift is not null;
    }

    private void InventorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_suppressInventoryFilterEvents && InventorySearchBox is not null)
            ApplyInventoryFilter();
    }

    private void InventoryFilters_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_suppressInventoryFilterEvents)
            ApplyInventoryFilter();
    }

    private void InventoryPageSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !_suppressInventoryFilterEvents)
            ApplyInventoryFilter();
    }

    private void RefreshInventoryCategories()
    {
        _suppressInventoryFilterEvents = true;
        var selectedCategory = InventoryCategoryFilterBox.SelectedItem as string;
        InventoryCategories.Clear();
        InventoryCategories.Add("Todas las categorías");
        foreach (var category in _allProducts
                     .Select(product => product.Category.Trim())
                     .Where(category => category.Length > 0)
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(category => category, StringComparer.CurrentCultureIgnoreCase))
        {
            InventoryCategories.Add(category);
        }

        InventoryCategoryFilterBox.SelectedItem = selectedCategory is not null
            && InventoryCategories.Contains(selectedCategory)
            ? selectedCategory
            : InventoryCategories[0];
        _suppressInventoryFilterEvents = false;
    }

    private void ApplyInventoryFilter()
    {
        var term = InventorySearchBox.Text.Trim();
        var selectedCategory = InventoryCategoryFilterBox.SelectedItem as string;
        var stockFilter = (InventoryStockFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var sortMode = (InventorySortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "name";

        IEnumerable<Product> products = _allProducts;
        if (!string.IsNullOrWhiteSpace(term))
        {
            products = products.Where(product => product.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || product.Barcode.Contains(term, StringComparison.OrdinalIgnoreCase)
                || product.Category.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(selectedCategory) && selectedCategory != "Todas las categorías")
            products = products.Where(product => product.Category.Equals(selectedCategory, StringComparison.CurrentCultureIgnoreCase));

        products = stockFilter switch
        {
            "low" => products.Where(product => product.Stock > 0 && product.Stock <= product.MinimumStock),
            "empty" => products.Where(product => product.Stock <= 0),
            "available" => products.Where(product => product.Stock > product.MinimumStock),
            _ => products
        };

        products = sortMode switch
        {
            "price-asc" => products.OrderBy(product => product.UnitPrice).ThenBy(product => product.Name),
            "price-desc" => products.OrderByDescending(product => product.UnitPrice).ThenBy(product => product.Name),
            "stock-asc" => products.OrderBy(product => product.Stock).ThenBy(product => product.Name),
            _ => products.OrderBy(product => product.Name, StringComparer.CurrentCultureIgnoreCase)
        };

        _filteredInventoryProducts = products.ToList();
        _inventoryCurrentPage = 1;
        RenderInventoryPage();
        RefreshInventorySummary();
    }

    private void RenderInventoryPage()
    {
        var pageSize = int.TryParse((InventoryPageSizeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var size)
            ? size
            : 8;
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filteredInventoryProducts.Count / (double)pageSize));
        _inventoryCurrentPage = Math.Clamp(_inventoryCurrentPage, 1, pageCount);

        InventoryProducts.Clear();
        foreach (var product in _filteredInventoryProducts.Skip((_inventoryCurrentPage - 1) * pageSize).Take(pageSize))
            InventoryProducts.Add(new InventoryProductCard(product));

        InventoryCardsList.SelectedItem = null;
        EditInventoryButton.IsEnabled = false;
        InventoryEmptyStatePanel.Visibility = InventoryProducts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var first = _filteredInventoryProducts.Count == 0 ? 0 : ((_inventoryCurrentPage - 1) * pageSize) + 1;
        var last = Math.Min(_inventoryCurrentPage * pageSize, _filteredInventoryProducts.Count);
        InventoryRangeText.Text = _filteredInventoryProducts.Count == 0
            ? "0 productos encontrados"
            : $"Mostrando {first:N0}–{last:N0} de {_filteredInventoryProducts.Count:N0} productos";
        InventoryPageText.Text = $"Página {_inventoryCurrentPage} de {pageCount}";
        InventoryPreviousPageButton.IsEnabled = _inventoryCurrentPage > 1;
        InventoryNextPageButton.IsEnabled = _inventoryCurrentPage < pageCount;
    }

    private void InventoryCardsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditInventoryButton.IsEnabled = InventoryCardsList.SelectedItem is InventoryProductCard;
    }

    private void InventoryPreviousPage_Click(object sender, RoutedEventArgs e)
    {
        if (_inventoryCurrentPage <= 1)
            return;
        _inventoryCurrentPage--;
        RenderInventoryPage();
    }

    private void InventoryNextPage_Click(object sender, RoutedEventArgs e)
    {
        var pageSize = int.TryParse((InventoryPageSizeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var size)
            ? size
            : 8;
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filteredInventoryProducts.Count / (double)pageSize));
        if (_inventoryCurrentPage >= pageCount)
            return;
        _inventoryCurrentPage++;
        RenderInventoryPage();
    }

    private void ClearInventoryFilters_Click(object sender, RoutedEventArgs e)
    {
        _suppressInventoryFilterEvents = true;
        InventorySearchBox.Clear();
        InventoryCategoryFilterBox.SelectedIndex = 0;
        InventoryStockFilterBox.SelectedIndex = 0;
        InventorySortBox.SelectedIndex = 0;
        _suppressInventoryFilterEvents = false;
        ApplyInventoryFilter();
    }

    private void RefreshInventorySummary()
    {
        if (!IsLoaded)
            return;
        var lowStock = _allProducts.Count(p => p.Stock <= p.MinimumStock);
        InventorySummaryText.Text = $"{_allProducts.Count} producto(s) activo(s) · {lowStock} con existencias bajas";
    }

    private async void NewProduct_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProductDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultProduct is null)
            return;
        await SaveProductFromDialogAsync(dialog.ResultProduct);
    }

    private async void EditProduct_Click(object sender, RoutedEventArgs e)
    {
        var selected = (InventoryCardsList.SelectedItem as InventoryProductCard)?.Product;
        if (selected is null)
        {
            MessageBox.Show("Selecciona una tarjeta de producto para editarla.", "Inventario", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new ProductDialog(selected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.ResultProduct is null)
            return;
        await SaveProductFromDialogAsync(dialog.ResultProduct);
    }

    private async Task SaveProductFromDialogAsync(Product product)
    {
        try
        {
            await PosService.SaveProductAsync(product);
            await ReloadProductsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Producto", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs || !IsLoaded)
            return;
        if (ReferenceEquals(MainTabs.SelectedItem, InventoryTab))
            await ReloadProductsAsync();
        else if (ReferenceEquals(MainTabs.SelectedItem, ShiftTab))
            await RefreshShiftPageAsync();
        else if (ReferenceEquals(MainTabs.SelectedItem, ReportsTab))
            await LoadReportsAsync();
        else if (ReferenceEquals(MainTabs.SelectedItem, AuditTab))
            await RefreshAuditAsync();
        else if (ReferenceEquals(MainTabs.SelectedItem, SyncTab))
            await RefreshConnectivityAsync();
        else if (ReferenceEquals(MainTabs.SelectedItem, UsersTab))
            await RefreshUsersAsync();
    }

    private async Task LoadReportsAsync()
    {
        var from = ReportFromDate.SelectedDate ?? DateTime.Today;
        var to = ReportToDate.SelectedDate ?? DateTime.Today;
        if (to.Date < from.Date)
        {
            (from, to) = (to, from);
            ReportFromDate.SelectedDate = from;
            ReportToDate.SelectedDate = to;
        }
        var serverSales = await BackendSyncService.GetServerSalesAsync(from, to);
        ReportsDataSourceText.Text = serverSales is null
            ? "Fuente: copia local"
            : "Fuente: servidor central";
        var sales = serverSales ?? await PosService.GetSalesBetweenAsync(from, to);
        if (serverSales is not null)
        {
            var pendingLocalSales = await PosService.GetPendingSalesBetweenAsync(from, to);
            var mergedSales = serverSales.ToDictionary(sale => sale.Id);
            foreach (var sale in pendingLocalSales)
                mergedSales[sale.Id] = sale;
            sales = mergedSales.Values.OrderByDescending(sale => sale.CreatedAtUtc).ToList();
        }

        var completed = sales.Where(sale => sale.Status == "Completada" || sale.Status == "Devuelta parcialmente").ToList();
        var payments = completed.SelectMany(sale => sale.Payments).ToList();
        var summary = new SalesSummary(
            completed.Count(sale => sale.Total > sale.ReturnedTotal),
            completed.Sum(sale => sale.Total - sale.ReturnedTotal),
            payments.Where(payment => payment.Method == "Efectivo").Sum(payment => payment.Amount) - completed.Sum(sale => sale.ReturnedTotal),
            payments.Where(payment => payment.Method == "Tarjeta").Sum(payment => payment.Amount),
            payments.Where(payment => payment.Method is not ("Efectivo" or "Tarjeta")).Sum(payment => payment.Amount));
        TodayCountText.Text = summary.SaleCount.ToString("N0");
        TodayTotalText.Text = summary.GrossSales.ToString("C0");
        TodayCashText.Text = summary.CashTotal.ToString("C0");
        TodayOtherText.Text = (summary.CardTotal + summary.OtherPayments).ToString("C0");
        RecentSalesGrid.ItemsSource = sales;
        CashierPerformanceGrid.ItemsSource = completed
            .GroupBy(sale => new { sale.CashierId, sale.CashierName })
            .Select(group =>
            {
                var payments = group.SelectMany(sale => sale.Payments).ToList();
                return new SalesByCashierRow(group.Key.CashierName, group.Count(), group.Sum(sale => sale.Total - sale.ReturnedTotal),
                    payments.Where(payment => payment.Method == "Efectivo").Sum(payment => payment.Amount),
                    payments.Where(payment => payment.Method != "Efectivo").Sum(payment => payment.Amount));
            })
            .OrderByDescending(row => row.GrossSales)
            .ToList();
        ShiftPerformanceGrid.ItemsSource = completed
            .GroupBy(sale => new { ShiftId = sale.CashShiftId ?? 0, sale.CashierName })
            .Select(group => new SalesByShiftRow(group.Key.ShiftId, group.Key.CashierName, group.Count(), group.Sum(sale => sale.Total - sale.ReturnedTotal)))
            .OrderByDescending(row => row.ShiftId)
            .ToList();
        var performance = serverSales is null
            ? await PosService.GetProductPerformanceAsync(from, to)
            : completed.SelectMany(sale => sale.Items)
                .GroupBy(item => new { item.ProductId, item.ProductName, item.Barcode })
                .Select(group => new ProductPerformanceRow(group.Key.ProductName, group.Key.Barcode,
                    group.Sum(item => item.Quantity - item.ReturnedQuantity), group.Sum(item => item.LineTotal - item.ReturnedAmount)))
                .OrderByDescending(row => row.QuantitySold)
                .ToList();
        ProductPerformanceGrid.ItemsSource = performance.Take(10).ToList();
        LowProductPerformanceGrid.ItemsSource = performance.OrderBy(row => row.QuantitySold).Take(10).ToList();
    }

    private async Task RefreshShiftPageAsync()
    {
        var userId = Session.CurrentUser?.Id ?? 0;
        _openShift = await PosService.GetOpenShiftAsync(userId);
        if (_openShift is null)
        {
            ShiftStatusText.Text = "Sin turno abierto";
            ShiftStatusText.Foreground = System.Windows.Media.Brushes.DarkOrange;
            ShiftOpeningText.Text = 0m.ToString("C0");
            ShiftExpectedText.Text = 0m.ToString("C0");
            ShiftOpenedText.Text = "--";
            ShiftMovementsGrid.ItemsSource = Array.Empty<CashMovement>();
        }
        else
        {
            ShiftStatusText.Text = "Turno abierto";
            ShiftStatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            ShiftOpeningText.Text = _openShift.OpeningFloat.ToString("C0");
            ShiftExpectedText.Text = (await PosService.CalculateExpectedCashAsync(_openShift.Id)).ToString("C0");
            ShiftOpenedText.Text = _openShift.OpenedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            ShiftMovementsGrid.ItemsSource = await PosService.GetCashMovementsAsync(_openShift.Id);
        }
        ShiftHistoryGrid.ItemsSource = await PosService.GetRecentCashShiftsAsync();
        ShiftButton.Content = _openShift is null ? "Abrir turno" : "Turno abierto";
        ChargeButtonState();
    }

    private async Task RefreshAuditAsync() =>
        AuditEventsGrid.ItemsSource = await PosService.GetRecentAuditEventsAsync();

    private async Task RefreshUsersAsync() => UsersGrid.ItemsSource = await PosService.GetUsersAsync();

    private async Task RefreshConnectivityAsync()
    {
        SyncQueueGrid.ItemsSource = await PosService.GetRecentSyncQueueAsync();
        var pendingCount = await PosService.GetPendingSyncCountAsync();
        PendingSyncCountText.Text = $"{pendingCount:N0} pendiente(s)";
        var backupDirectory = Path.Combine(Path.GetDirectoryName(App.DatabasePath)!, "backups");
        var latestBackup = Directory.Exists(backupDirectory)
            ? new DirectoryInfo(backupDirectory).GetFiles("pos-*.db").OrderByDescending(file => file.LastWriteTime).FirstOrDefault()
            : null;
        LatestBackupText.Text = latestBackup is null
            ? "No disponible"
            : latestBackup.LastWriteTime.ToString("dd/MM/yyyy HH:mm");
    }

    private async Task LoadBackendSettingsAsync()
    {
        var settings = await BackendSettingsService.LoadAsync();
        BackendUrlBox.Text = settings.BaseUrl;
        BackendApiKeyBox.Password = settings.ApiKey;
    }

    private async Task SynchronizeAndRefreshAsync()
    {
        var result = await BackendSyncService.SynchronizePendingAsync();
        SyncStatusText.Text = result.Message;
        BackendConnectionText.Text = result.IsConnected
            ? $"API central conectada · Identificador de esta caja: {(await BackendSettingsService.LoadAsync()).ClientId[..8]}"
            : result.Message;
        if (result.IsConnected)
        {
            await ReloadProductsAsync();
            if (ReferenceEquals(MainTabs.SelectedItem, ReportsTab))
                await LoadReportsAsync();
        }
        await RefreshConnectivityAsync();
    }

    private async void SaveBackendSettings_Click(object sender, RoutedEventArgs e)
    {
        var url = BackendUrlBox.Text.Trim();
        if (!string.IsNullOrEmpty(url)
            && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                || parsed.Scheme is not ("http" or "https")))
        {
            MessageBox.Show("Ingresa una URL completa que empiece por http:// o https://.",
                "Conexión central", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await BackendSettingsService.SaveAsync(url, BackendApiKeyBox.Password);
            BackendConnectionText.Text = "Conexión guardada. Comprobando la API central…";
            await SynchronizeAndRefreshAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "No se pudo guardar la conexión", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        SyncNowButton.IsEnabled = false;
        try
        {
            await SynchronizeAndRefreshAsync();
        }
        finally
        {
            SyncNowButton.IsEnabled = true;
        }
    }

    private async void RefreshReport_Click(object sender, RoutedEventArgs e) => await LoadReportsAsync();

    private async void RefreshShiftPage_Click(object sender, RoutedEventArgs e) => await RefreshShiftPageAsync();

    private async void RefreshAudit_Click(object sender, RoutedEventArgs e) => await RefreshAuditAsync();

    private async void RefreshConnectivity_Click(object sender, RoutedEventArgs e) => await RefreshConnectivityAsync();

    private async void NewUser_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new UserAdminDialog { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            await PosService.CreateUserAsync(dialog.Username, dialog.DisplayName, dialog.Role, dialog.Pin);
            await RefreshUsersAsync();
            await RefreshAuditAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo crear el usuario", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ResetUserPin_Click(object sender, RoutedEventArgs e)
    {
        if (UsersGrid.SelectedItem is not PosUserSummary user)
        {
            MessageBox.Show("Selecciona un usuario.", "Usuarios", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new UserAdminDialog(user) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            await PosService.ResetUserPinAsync(user.Id, dialog.Pin);
            await RefreshAuditAsync();
            MessageBox.Show("El PIN quedó restablecido.", "Usuarios", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo restablecer el PIN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ToggleUserActive_Click(object sender, RoutedEventArgs e)
    {
        if (UsersGrid.SelectedItem is not PosUserSummary user)
        {
            MessageBox.Show("Selecciona un usuario.", "Usuarios", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await PosService.SetUserActiveAsync(user.Id, !user.IsActive);
            await RefreshUsersAsync();
            await RefreshAuditAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo cambiar el estado", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F2:
                MainTabs.SelectedItem = SalesTab;
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.F3:
                MainTabs.SelectedItem = SalesTab;
                BarcodeBox.Focus();
                BarcodeBox.SelectAll();
                e.Handled = true;
                break;
            case Key.F4:
                Charge_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F7:
                await HandleShiftAsync();
                e.Handled = true;
                break;
            case Key.F8:
                SuspendSale_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.F9:
                ResumeSale_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private async void ReprintSale_Click(object sender, RoutedEventArgs e)
    {
        if (RecentSalesGrid.SelectedItem is not Sale selected)
            return;
        var sale = await PosService.GetSaleAsync(selected.Id);
        if (sale is null)
            return;
        var settings = await PeripheralSettingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.PrinterName))
            ReceiptPrintService.PrintSale(this, sale);
        else
        {
            try
            {
                await new WindowsEscPosPeripheralService().PrintSaleAsync(sale);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, "No se pudo reimprimir", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void ReturnSale_Click(object sender, RoutedEventArgs e)
    {
        if (RecentSalesGrid.SelectedItem is not Sale selected
            || selected.Status is not ("Completada" or "Devuelta parcialmente"))
        {
            MessageBox.Show("Selecciona una venta con productos pendientes de devolución.", "Devolución",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sale = await PosService.GetSaleAsync(selected.Id);
        if (sale is null)
            return;
        var dialog = new SaleReturnDialog(sale) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var approval = await RequestSupervisorAsync($"Autorizar devolución de productos de la venta #{sale.Id} por aproximadamente {dialog.EstimatedTotal:C0}.");
        if (approval is null)
            return;
        if (MessageBox.Show($"¿Confirmar la devolución de {dialog.EstimatedTotal:C0} de la venta #{sale.Id}?",
                "Confirmar devolución", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var returned = await PosService.ReturnSaleItemsAsync(sale.Id, dialog.RequestedLines, approval, dialog.ReturnReason);
            await ReloadProductsAsync();
            await RefreshShiftPageAsync();
            await LoadReportsAsync();
            await RefreshAuditAsync();
            MessageBox.Show($"Devolución #{returned.Id} registrada por {returned.Total:C0}. Se actualizó el inventario y la caja.",
                "Devolución completada", MessageBoxButton.OK, MessageBoxImage.Information);
            var fiscalDocuments = await PosService.GetFiscalDocumentsAsync(sale.Id);
            if (fiscalDocuments.Any(document => document.Kind == "Factura" && document.Status == "Emitida")
                && MessageBox.Show("La venta tiene factura electrónica. ¿Deseas emitir la nota crédito de esta devolución ahora?",
                    "Nota crédito", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await IssueCreditNoteAsync(sale.Id, returned.Id);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "No se pudo completar la devolución", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ConfigureDevices_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PeripheralSettingsDialog { Owner = this };
        dialog.ShowDialog();
    }

    private async void IssueInvoice_Click(object sender, RoutedEventArgs e)
    {
        if (RecentSalesGrid.SelectedItem is not Sale selected)
            return;
        if (selected.Status is "Anulada" or "Devuelta")
        {
            MessageBox.Show("No se puede facturar una venta anulada o devuelta por completo.", "Factura electrónica",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var documents = await PosService.GetFiscalDocumentsAsync(selected.Id);
        var existing = documents.FirstOrDefault(document => document.Kind == "Factura" && document.Status == "Emitida");
        if (existing is not null)
        {
            MessageBox.Show($"La venta ya tiene la factura {existing.DocumentNumber}.", "Factura electrónica",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var issued = await FiscalInvoicingClient.IssueInvoiceAsync(selected.Id);
            await PosService.SaveFiscalDocumentAsync(new FiscalDocument
            {
                SaleId = selected.Id,
                Kind = "Factura",
                Status = issued.Status,
                Provider = issued.Provider,
                DocumentNumber = issued.DocumentNumber,
                ProviderDocumentId = issued.ProviderDocumentId,
                ResponsePayload = issued.ResponsePayload
            });
            MessageBox.Show($"Factura electrónica emitida: {issued.DocumentNumber}.", "Factura electrónica",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            await SaveFiscalFailureAsync(selected.Id, null, "Factura", exception.Message);
            MessageBox.Show(exception.Message, "No se pudo emitir la factura", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void IssueCreditNote_Click(object sender, RoutedEventArgs e)
    {
        if (RecentSalesGrid.SelectedItem is not Sale selected)
            return;
        var returns = await PosService.GetSaleReturnsAsync(selected.Id);
        if (returns.Count == 0)
        {
            MessageBox.Show("La venta todavía no tiene devoluciones registradas.", "Nota crédito",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await IssueCreditNoteAsync(selected.Id, returns[0].Id);
    }

    private async Task IssueCreditNoteAsync(long saleId, long returnId)
    {
        var documents = await PosService.GetFiscalDocumentsAsync(saleId);
        if (!documents.Any(document => document.Kind == "Factura" && document.Status == "Emitida"))
        {
            MessageBox.Show("Emite primero la factura electrónica original.", "Nota crédito",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (documents.Any(document => document.Kind == "Nota crédito" && document.SaleReturnId == returnId && document.Status == "Emitida"))
        {
            MessageBox.Show("Esta devolución ya tiene una nota crédito emitida.", "Nota crédito",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var issued = await FiscalInvoicingClient.IssueCreditNoteAsync(saleId, returnId);
            await PosService.SaveFiscalDocumentAsync(new FiscalDocument
            {
                SaleId = saleId,
                SaleReturnId = returnId,
                Kind = "Nota crédito",
                Status = issued.Status,
                Provider = issued.Provider,
                DocumentNumber = issued.DocumentNumber,
                ProviderDocumentId = issued.ProviderDocumentId,
                ResponsePayload = issued.ResponsePayload
            });
            MessageBox.Show($"Nota crédito emitida: {issued.DocumentNumber}.", "Nota crédito",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            await SaveFiscalFailureAsync(saleId, returnId, "Nota crédito", exception.Message);
            MessageBox.Show(exception.Message, "No se pudo emitir la nota crédito", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static async Task SaveFiscalFailureAsync(long saleId, long? returnId, string kind, string error)
    {
        try
        {
            await PosService.SaveFiscalDocumentAsync(new FiscalDocument
            {
                SaleId = saleId,
                SaleReturnId = returnId,
                Kind = kind,
                Status = "Fallida",
                Error = error
            });
        }
        catch
        {
        }
    }

    private async void VoidSale_Click(object sender, RoutedEventArgs e)
    {
        if (RecentSalesGrid.SelectedItem is not Sale selected || selected.Status != "Completada")
        {
            MessageBox.Show("Selecciona una venta completada del historial.", "Anular venta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sale = await PosService.GetSaleAsync(selected.Id);
        if (sale is null)
            return;
        var approval = await RequestSupervisorAsync($"Autorizar la devolución total de la venta #{sale.Id} y reponer sus productos.");
        if (approval is null)
            return;
        if (MessageBox.Show($"¿Devolver todos los productos de la venta #{sale.Id} por {sale.Total:C0}?",
                "Confirmar anulación", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            var returned = await PosService.ReturnSaleItemsAsync(sale.Id,
                sale.Items.Select(item => new SaleReturnLineDraft(item.Id, item.Quantity - item.ReturnedQuantity)).ToList(),
                approval, "Anulación total de venta");
            await ReloadProductsAsync();
            await RefreshShiftPageAsync();
            await LoadReportsAsync();
            await RefreshAuditAsync();
            MessageBox.Show($"Devolución total registrada por {returned.Total:C0}; el inventario y la caja se actualizaron.", "Anulación completada",
                MessageBoxButton.OK, MessageBoxImage.Information);
            var documents = await PosService.GetFiscalDocumentsAsync(sale.Id);
            if (documents.Any(document => document.Kind == "Factura" && document.Status == "Emitida")
                && MessageBox.Show("La venta tiene factura electrónica. ¿Emitir la nota crédito de esta devolución?",
                    "Nota crédito", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await IssueCreditNoteAsync(sale.Id, returned.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo anular la venta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public sealed class InventoryProductCard(Product product)
{
    private static readonly Brush EmptyBackground = CreateBrush(0x49, 0x2A, 0x30);
    private static readonly Brush EmptyForeground = CreateBrush(0xFF, 0xB4, 0xAB);
    private static readonly Brush LowBackground = CreateBrush(0x43, 0x36, 0x24);
    private static readonly Brush LowForeground = CreateBrush(0xFF, 0xD1, 0x8B);
    private static readonly Brush AvailableBackground = CreateBrush(0x22, 0x36, 0x50);
    private static readonly Brush AvailableForeground = CreateBrush(0x9F, 0xC1, 0xEF);

    public Product Product { get; } = product;
    public string Name => Product.Name;
    public string Barcode => Product.Barcode;
    public string Category => string.IsNullOrWhiteSpace(Product.Category) ? "Sin categoría" : Product.Category;
    public string Unit => Product.Unit;
    public string ImagePath => Product.ImagePath;
    public decimal UnitPrice => Product.UnitPrice;
    public decimal Stock => Product.Stock;
    public decimal MinimumStock => Product.MinimumStock;
    public string StockStatus => Stock <= 0 ? "Agotado" : Stock <= MinimumStock ? "Reponer" : "Disponible";
    public Brush StockStatusBackground => Stock <= 0 ? EmptyBackground : Stock <= MinimumStock ? LowBackground : AvailableBackground;
    public Brush StockStatusForeground => Stock <= 0 ? EmptyForeground : Stock <= MinimumStock ? LowForeground : AvailableForeground;

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
