using Shop.App.Infrastructure;
using Shop.Domain.Billing;
using Shop.Domain.Orders;

namespace Shop.App;

public interface IOrderService
{
    Invoice Checkout(int orderId);
}

public class OrderService(IOrderRepository repository) : IOrderService
{
    // Deliberate rule violation: a convenient shortcut that ties the service to one storage implementation.
    public static OrderService CreateDefault() => new(new InMemoryOrderRepository());

    public Invoice Checkout(int orderId)
    {
        var order = repository.Find(orderId) ?? throw new InvalidOperationException($"Order {orderId} not found.");
        repository.Save(order);
        return new Invoice(order);
    }
}
