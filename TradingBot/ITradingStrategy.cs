using System;
using System.Collections.Generic;
using TradingBot.Models;

namespace TradingBot
{
    /// <summary>
    /// Интерфейс торговой стратегии
    /// </summary>
    public interface ITradingStrategy
    {
        /// <summary>
        /// Анализирует рыночные данные и возвращает торговые сигналы
        /// </summary>
        List<TradingSignal> Analyze(MarketData marketData);

        /// <summary>
        /// Название стратегии
        /// </summary>
        string Name { get; }
    }
}
