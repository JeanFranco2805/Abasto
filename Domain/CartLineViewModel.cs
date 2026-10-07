using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SupermercadoPOS.Domain;

public sealed class CartLineViewModel : INotifyPropertyChanged
{
    private decimal _quantity;
    private decimal _discountRate;
    private string _discountApprovedBy = "";

    public int ProductId { get; }
    public string Name { get; }
    public string Barcode { get; }
    public string Unit { get; }
    public decimal UnitPrice { get; }
    public decimal TaxRate { get; }
    public bool IsWeighted => ProductUnitRules.AllowsFractionalQuantity(Unit);

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (_quantity == value)
                return;
            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(QuantityText));
            OnPropertyChanged(nameof(LineSubtotal));
            OnPropertyChanged(nameof(DiscountAmount));
            OnPropertyChanged(nameof(DiscountLabel));
            OnPropertyChanged(nameof(LineTax));
            OnPropertyChanged(nameof(LineTotal));
        }
    }

    public string QuantityText => IsWeighted ? $"{Quantity:0.000} {Unit}" : $"{Quantity:0} {Unit}";
    public decimal LineSubtotal => Money(UnitPrice * Quantity);
    public decimal DiscountRate
    {
        get => _discountRate;
        set
        {
            if (_discountRate == value)
                return;
            _discountRate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiscountAmount));
            OnPropertyChanged(nameof(DiscountLabel));
            OnPropertyChanged(nameof(LineTax));
            OnPropertyChanged(nameof(LineTotal));
        }
    }
    public string DiscountApprovedBy
    {
        get => _discountApprovedBy;
        set
        {
            _discountApprovedBy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiscountLabel));
        }
    }
    public decimal DiscountAmount => Money(LineSubtotal * DiscountRate);
    public string DiscountLabel => DiscountRate == 0
        ? "Sin descuento"
        : $"Desc. {DiscountRate:P0} ({DiscountAmount:C0}) · {DiscountApprovedBy}";
    public decimal LineTax => Money((LineSubtotal - DiscountAmount) * TaxRate);
    public decimal LineTotal => Money(LineSubtotal - DiscountAmount + LineTax);

    public event PropertyChangedEventHandler? PropertyChanged;

    public CartLineViewModel(Product product, decimal quantity)
    {
        ProductId = product.Id;
        Name = product.Name;
        Barcode = product.Barcode;
        Unit = product.Unit;
        UnitPrice = product.UnitPrice;
        TaxRate = product.TaxRate;
        _quantity = quantity;
    }

    public CartLineViewModel(SaleItem item)
    {
        ProductId = item.ProductId;
        Name = item.ProductName;
        Barcode = item.Barcode;
        Unit = item.Unit;
        UnitPrice = item.UnitPrice;
        TaxRate = item.TaxRate;
        _quantity = item.Quantity;
        _discountRate = item.LineSubtotal == 0 ? 0 : item.DiscountAmount / item.LineSubtotal;
        _discountApprovedBy = item.DiscountApprovedBy;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static decimal Money(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
