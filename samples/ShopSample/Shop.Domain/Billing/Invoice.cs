using Shop.Domain.Common;
using Shop.Domain.Orders;

namespace Shop.Domain.Billing;

public class Invoice(Order order)
{
    public Order Order { get; } = order;
    public Money Amount => Order.Total();
}

public static class TaxCalculator
{
    public static Money WithTax(Money net) => net with { Amount = net.Amount * 1.0945m };
}
