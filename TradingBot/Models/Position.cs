using System;

namespace TradingBot.Models
{
    /// <summary>
    /// Торговая позиция
    /// </summary>
    public class Position
    {
        public string Symbol { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal CurrentPrice { get; set; }
        public DateTime OpenTime { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public decimal RealizedPnl { get; set; }
        public PositionSide Side { get; set; }

        /// <summary>
        /// Рассчитывает текущий PnL
        /// </summary>
        public void UpdatePnl()
        {
            if (Side == PositionSide.Long)
            {
                UnrealizedPnl = (CurrentPrice - EntryPrice) * Quantity;
            }
            else if (Side == PositionSide.Short)
            {
                UnrealizedPnl = (EntryPrice - CurrentPrice) * Quantity;
            }
        }
    }

    /// <summary>
    /// Сторона позиции
    /// </summary>
    public enum PositionSide
    {
        Long,   // Длинная позиция
        Short   // Короткая позиция
    }
}
