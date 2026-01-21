using System;

namespace TradingBot.Models
{
    /// <summary>
    /// Тип ордера
    /// </summary>
    public enum OrderType
    {
        Market,     // Рыночный ордер
        Limit,      // Лимитный ордер
        StopLoss,   // Стоп-лосс
        TakeProfit  // Тейк-профит
    }

    /// <summary>
    /// Статус ордера
    /// </summary>
    public enum OrderStatus
    {
        Pending,    // Ожидает исполнения
        Filled,     // Исполнен
        Cancelled,  // Отменён
        Rejected    // Отклонён
    }

    /// <summary>
    /// Торговый ордер
    /// </summary>
    public class Order
    {
        public string Id { get; set; }
        public string Symbol { get; set; }
        public OrderType Type { get; set; }
        public OrderSide Side { get; set; }
        public decimal Quantity { get; set; }
        public decimal Price { get; set; }
        public decimal? StopPrice { get; set; }
        public OrderStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? FilledAt { get; set; }
        public decimal? FilledPrice { get; set; }
        public decimal? FilledQuantity { get; set; }

        public Order()
        {
            Id = Guid.NewGuid().ToString();
            Status = OrderStatus.Pending;
            CreatedAt = DateTime.Now;
        }
    }

    /// <summary>
    /// Сторона ордера
    /// </summary>
    public enum OrderSide
    {
        Buy,
        Sell
    }
}
