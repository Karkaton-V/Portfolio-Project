using Shop.App;

namespace Shop.Api;

public class OrdersController(IOrderService orders)
{
    public decimal Checkout(int id) => orders.Checkout(id).Amount.Amount;
}
