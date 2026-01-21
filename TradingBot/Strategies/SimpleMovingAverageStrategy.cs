using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot.Strategies
{
    /// <summary>
    /// Простая стратегия на основе скользящих средних
    /// </summary>
    public class SimpleMovingAverageStrategy : ITradingStrategy
    {
        private readonly int _shortPeriod;
        private readonly int _longPeriod;
        private readonly decimal _minConfidence;

        public string Name => "SMA Strategy";

        public SimpleMovingAverageStrategy(int shortPeriod = 10, int longPeriod = 30, decimal minConfidence = 0.6m)
        {
            _shortPeriod = shortPeriod;
            _longPeriod = longPeriod;
            _minConfidence = minConfidence;
        }

        public List<TradingSignal> Analyze(MarketData marketData)
        {
            var signals = new List<TradingSignal>();

            if (marketData.Candles.Count < _longPeriod)
            {
                return signals; // Недостаточно данных
            }

            // Рассчитываем скользящие средние
            var closes = marketData.Candles.Select(c => c.Close).ToList();
            var shortMA = CalculateSMA(closes, _shortPeriod);
            var longMA = CalculateSMA(closes, _longPeriod);

            var currentPrice = marketData.Price;
            var lastShortMA = shortMA.Last();
            var lastLongMA = longMA.Last();
            var prevShortMA = shortMA.Count > 1 ? shortMA[shortMA.Count - 2] : lastShortMA;
            var prevLongMA = longMA.Count > 1 ? longMA[longMA.Count - 2] : lastLongMA;

            // Сигнал на покупку: короткая MA пересекает длинную снизу вверх
            if (prevShortMA <= prevLongMA && lastShortMA > lastLongMA)
            {
                decimal confidence = CalculateConfidence(currentPrice, lastShortMA, lastLongMA);
                if (confidence >= _minConfidence)
                {
                    signals.Add(new TradingSignal
                    {
                        Type = SignalType.Buy,
                        Symbol = marketData.Symbol,
                        Price = currentPrice,
                        Confidence = confidence,
                        Reason = $"SMA crossover: Short MA ({lastShortMA:F2}) crossed above Long MA ({lastLongMA:F2})"
                    });
                }
            }
            // Сигнал на продажу: короткая MA пересекает длинную сверху вниз
            else if (prevShortMA >= prevLongMA && lastShortMA < lastLongMA)
            {
                decimal confidence = CalculateConfidence(currentPrice, lastShortMA, lastLongMA);
                if (confidence >= _minConfidence)
                {
                    signals.Add(new TradingSignal
                    {
                        Type = SignalType.Sell,
                        Symbol = marketData.Symbol,
                        Price = currentPrice,
                        Confidence = confidence,
                        Reason = $"SMA crossover: Short MA ({lastShortMA:F2}) crossed below Long MA ({lastLongMA:F2})"
                    });
                }
            }

            return signals;
        }

        private List<decimal> CalculateSMA(List<decimal> prices, int period)
        {
            var sma = new List<decimal>();
            for (int i = period - 1; i < prices.Count; i++)
            {
                decimal sum = 0;
                for (int j = i - period + 1; j <= i; j++)
                {
                    sum += prices[j];
                }
                sma.Add(sum / period);
            }
            return sma;
        }

        private decimal CalculateConfidence(decimal price, decimal shortMA, decimal longMA)
        {
            // Уверенность зависит от расстояния между MA и текущей ценой
            decimal maDiff = Math.Abs(shortMA - longMA);
            decimal priceDiff = Math.Abs(price - (shortMA + longMA) / 2);
            
            if (maDiff == 0) return 0.5m;
            
            decimal confidence = Math.Min(1.0m, maDiff / (price * 0.01m)); // Нормализация
            return Math.Max(0.5m, Math.Min(1.0m, confidence));
        }
    }
}
