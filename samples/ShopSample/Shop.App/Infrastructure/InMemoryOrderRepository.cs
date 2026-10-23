using Shop.Domain.Orders;

namespace Shop.App.Infrastructure;

public class InMemoryOrderRepository : IOrderRepository
{
    private readonly Dictionary<int, Order> _orders = [];

    public Order? Find(int id) => _orders.GetValueOrDefault(id);

    public void Save(Order order) => _orders[order.Id] = order;
}
