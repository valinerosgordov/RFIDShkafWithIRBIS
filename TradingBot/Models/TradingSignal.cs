using System;

namespace TradingBot.Models
{
    /// <summary>
    /// Тип торгового сигнала
    /// </summary>
    public enum SignalType
    {
        Buy,      // Покупка
        Sell,     // Продажа
        Hold      // Удержание позиции
    }

    /// <summary>
    /// Торговый сигнал
    /// </summary>
    public class TradingSignal
    {
        public SignalType Type { get; set; }
        public string Symbol { get; set; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
        public decimal Confidence { get; set; } // 0.0 - 1.0
        public string Reason { get; set; }
        public DateTime Timestamp { get; set; }

        public TradingSignal()
        {
            Timestamp = DateTime.Now;
            Confidence = 0.5m;
        }
    }
}
