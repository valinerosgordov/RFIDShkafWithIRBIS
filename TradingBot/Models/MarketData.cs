using System;
using System.Collections.Generic;

namespace TradingBot.Models
{
    /// <summary>
    /// Рыночные данные
    /// </summary>
    public class MarketData
    {
        public string Symbol { get; set; }
        public decimal Price { get; set; }
        public decimal Volume { get; set; }
        public DateTime Timestamp { get; set; }
        public decimal High24h { get; set; }
        public decimal Low24h { get; set; }
        public decimal Change24h { get; set; }
        public List<Candle> Candles { get; set; }

        public MarketData()
        {
            Candles = new List<Candle>();
            Timestamp = DateTime.Now;
        }
    }

    /// <summary>
    /// Свеча (OHLCV)
    /// </summary>
    public class Candle
    {
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
    }
}
