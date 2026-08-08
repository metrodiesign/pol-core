using Orders.Domain;

namespace Orders.Application;

/// <summary>Narrow owner port used only by host-composed Cart-to-Order transaction.</summary>
public interface IOrderStore
{
    void Add(Order order);
}
