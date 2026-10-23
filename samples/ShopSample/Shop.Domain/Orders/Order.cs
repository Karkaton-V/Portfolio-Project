using Shop.Domain.Billing;
using Shop.Domain.Common;

namespace Shop.Domain.Orders;

public class Order
{
    public int Id { get; init; }
    public List<OrderLine> Lines { get; } = [];

    // Deliberate tangle: Orders reaches into Billing, and Billing (Invoice) points back at Orders.
    public Money Total() => TaxCalculator.WithTax(new Money(Lines.Sum(l => l.Price.Amount), "USD"));
}

public class OrderLine
{
    public required Order Order { get; init; }
    public required Money Price { get; init; }
}

public interface IOrderRepository
{
    Order? Find(int id);
    void Save(Order order);
}
