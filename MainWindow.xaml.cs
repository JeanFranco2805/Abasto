using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SupermercadoPOS.Dialogs;
using SupermercadoPOS.Domain;
using SupermercadoPOS.Services;

namespace SupermercadoPOS;

public partial class MainWindow : Window
{
    private readonly List<Product> _allProducts = [];
    private List<Product> _filteredInventoryProducts = [];
    private int _inventoryCurrentPage = 1;
    private bool _suppressInventoryFilterEvents;
    private CashShift? _openShift;
    private long? _currentSuspendedSaleId;

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
        Loaded += async (_, _) =>
        {
            ReportFromDate.SelectedDate = DateTime.Today;
            ReportToDate.SelectedDate = DateTime.Today;
            await ReloadProductsAsync();
            await RefreshShiftAsync();
            RefreshTotals();
            await RefreshShiftPageAsync();
            MainTabs.SelectedItem = SalesTab;
        };
        Cart.CollectionChanged += (_, _) => RefreshTotals();
    }

    private async Task ReloadProductsAsync()
    {
        var products = await PosService.GetProductsAsync();
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

    private void AddToCart(Product product)
    {
        var quantityStep = product.Unit == "KG" ? 0.1m : 1m;
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
                    ReceiptPrintService.PrintSale(this, completedSale);
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
        var summary = await PosService.GetSalesSummaryBetweenAsync(from, to);
        TodayCountText.Text = summary.SaleCount.ToString("N0");
        TodayTotalText.Text = summary.GrossSales.ToString("C0");
        TodayCashText.Text = summary.CashTotal.ToString("C0");
        TodayOtherText.Text = (summary.CardTotal + summary.OtherPayments).ToString("C0");
        var sales = await PosService.GetSalesBetweenAsync(from, to);
        RecentSalesGrid.ItemsSource = sales;
        var completed = sales.Where(sale => sale.Status == "Completada").ToList();
        CashierPerformanceGrid.ItemsSource = completed
            .GroupBy(sale => new { sale.CashierId, sale.CashierName })
            .Select(group =>
            {
                var payments = group.SelectMany(sale => sale.Payments).ToList();
                return new SalesByCashierRow(group.Key.CashierName, group.Count(), group.Sum(sale => sale.Total),
                    payments.Where(payment => payment.Method == "Efectivo").Sum(payment => payment.Amount),
                    payments.Where(payment => payment.Method != "Efectivo").Sum(payment => payment.Amount));
            })
            .OrderByDescending(row => row.GrossSales)
            .ToList();
        ShiftPerformanceGrid.ItemsSource = completed
            .GroupBy(sale => new { ShiftId = sale.CashShiftId ?? 0, sale.CashierName })
            .Select(group => new SalesByShiftRow(group.Key.ShiftId, group.Key.CashierName, group.Count(), group.Sum(sale => sale.Total)))
            .OrderByDescending(row => row.ShiftId)
            .ToList();
        var performance = await PosService.GetProductPerformanceAsync(from, to);
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
        if (sale is not null)
            ReceiptPrintService.PrintSale(this, sale);
    }

    private async void VoidSale_Click(object sender, RoutedEventArgs e)
    {
        if (RecentSalesGrid.SelectedItem is not Sale selected || selected.Status != "Completada")
        {
            MessageBox.Show("Selecciona una venta completada del historial.", "Anular venta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var approval = await RequestSupervisorAsync($"La venta #{selected.Id} se devolverá en efectivo y se repondrán sus productos.");
        if (approval is null)
            return;
        if (MessageBox.Show($"¿Anular la venta #{selected.Id} por {selected.Total:C0}?",
                "Confirmar anulación", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            await PosService.VoidCompletedCashSaleAsync(selected.Id, approval);
            await ReloadProductsAsync();
            await LoadReportsAsync();
            MessageBox.Show("La venta fue anulada y el inventario repuesto.", "Anulación completada",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "No se pudo anular la venta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public sealed class InventoryProductCard(Product product)
{
    private static readonly Brush EmptyBackground = CreateBrush(0xFE, 0xF3, 0xF2);
    private static readonly Brush EmptyForeground = CreateBrush(0xB4, 0x23, 0x18);
    private static readonly Brush LowBackground = CreateBrush(0xFF, 0xFA, 0xEB);
    private static readonly Brush LowForeground = CreateBrush(0xB5, 0x47, 0x08);
    private static readonly Brush AvailableBackground = CreateBrush(0xEC, 0xFD, 0xF3);
    private static readonly Brush AvailableForeground = CreateBrush(0x02, 0x7A, 0x48);

    public Product Product { get; } = product;
    public string Name => Product.Name;
    public string Barcode => Product.Barcode;
    public string Category => string.IsNullOrWhiteSpace(Product.Category) ? "Sin categoría" : Product.Category;
    public string Unit => Product.Unit;
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
