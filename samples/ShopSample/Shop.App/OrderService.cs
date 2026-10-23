using Shop.Domain.Billing;
using Shop.Domain.Orders;

namespace Shop.App;

public interface IOrderService
{
    Invoice Checkout(int orderId);
}

public class OrderService(IOrderRepository repository) : IOrderService
{
    public Invoice Checkout(int orderId)
    {
        var order = repository.Find(orderId) ?? throw new InvalidOperationException($"Order {orderId} not found.");
        repository.Save(order);
        return new Invoice(order);
    }
}

public class InMemoryOrderRepository : IOrderRepository
{
    private readonly Dictionary<int, Order> _orders = [];

    public Order? Find(int id) => _orders.GetValueOrDefault(id);

    public void Save(Order order) => _orders[order.Id] = order;
}
