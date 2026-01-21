using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Models;

namespace TradingBot
{
    /// <summary>
    /// Менеджер управления рисками
    /// </summary>
    public class RiskManager
    {
        private readonly decimal _maxPositionSize;
        private readonly decimal _maxDailyLoss;
        private readonly decimal _stopLossPercent;
        private readonly decimal _takeProfitPercent;
        private decimal _dailyPnL;

        public RiskManager(decimal maxPositionSize = 0.1m, decimal maxDailyLoss = 0.05m, 
                          decimal stopLossPercent = 0.02m, decimal takeProfitPercent = 0.05m)
        {
            _maxPositionSize = maxPositionSize;
            _maxDailyLoss = maxDailyLoss;
            _stopLossPercent = stopLossPercent;
            _takeProfitPercent = takeProfitPercent;
            _dailyPnL = 0;
        }

        /// <summary>
        /// Проверяет, можно ли открыть позицию
        /// </summary>
        public bool CanOpenPosition(decimal accountBalance, decimal positionSize, List<Position> existingPositions)
        {
            // Проверка максимального размера позиции
            if (positionSize > accountBalance * _maxPositionSize)
            {
                return false;
            }

            // Проверка дневного лимита убытков
            if (_dailyPnL <= -_maxDailyLoss * accountBalance)
            {
                return false;
            }

            // Проверка общего размера всех позиций
            decimal totalExposure = existingPositions.Sum(p => p.Quantity * p.EntryPrice);
            if (totalExposure + positionSize > accountBalance * 0.5m) // Максимум 50% капитала в позициях
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Рассчитывает размер позиции на основе риска
        /// </summary>
        public decimal CalculatePositionSize(decimal accountBalance, decimal entryPrice, decimal stopLossPrice)
        {
            decimal riskAmount = accountBalance * _maxPositionSize;
            decimal priceRisk = Math.Abs(entryPrice - stopLossPrice);
            
            if (priceRisk <= 0) return 0;
            
            return riskAmount / priceRisk;
        }

        /// <summary>
        /// Проверяет, нужно ли закрыть позицию по стоп-лоссу
        /// </summary>
        public bool ShouldStopLoss(Position position)
        {
            position.UpdatePnl();
            decimal lossPercent = Math.Abs(position.UnrealizedPnl) / (position.EntryPrice * position.Quantity);
            return lossPercent >= _stopLossPercent;
        }

        /// <summary>
        /// Проверяет, нужно ли закрыть позицию по тейк-профиту
        /// </summary>
        public bool ShouldTakeProfit(Position position)
        {
            position.UpdatePnl();
            decimal profitPercent = position.UnrealizedPnl / (position.EntryPrice * position.Quantity);
            return profitPercent >= _takeProfitPercent;
        }

        /// <summary>
        /// Обновляет дневной PnL
        /// </summary>
        public void UpdateDailyPnL(decimal realizedPnL)
        {
            _dailyPnL += realizedPnL;
        }

        /// <summary>
        /// Сбрасывает дневной PnL (вызывать в начале дня)
        /// </summary>
        public void ResetDailyPnL()
        {
            _dailyPnL = 0;
        }
    }
}
