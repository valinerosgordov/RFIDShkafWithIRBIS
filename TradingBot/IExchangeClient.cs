using System.Collections.Generic;
using System.Threading.Tasks;
using TradingBot.Models;

namespace TradingBot
{
    /// <summary>
    /// Интерфейс клиента биржи
    /// </summary>
    public interface IExchangeClient
    {
        /// <summary>
        /// Получить текущую цену символа
        /// </summary>
        Task<decimal> GetPriceAsync(string symbol);

        /// <summary>
        /// Получить рыночные данные
        /// </summary>
        Task<MarketData> GetMarketDataAsync(string symbol);

        /// <summary>
        /// Разместить ордер на покупку
        /// </summary>
        Task<Order> PlaceBuyOrderAsync(string symbol, decimal quantity, decimal? price = null);

        /// <summary>
        /// Разместить ордер на продажу
        /// </summary>
        Task<Order> PlaceSellOrderAsync(string symbol, decimal quantity, decimal? price = null);

        /// <summary>
        /// Отменить ордер
        /// </summary>
        Task<bool> CancelOrderAsync(string orderId);

        /// <summary>
        /// Получить список открытых позиций
        /// </summary>
        Task<List<Position>> GetPositionsAsync();

        /// <summary>
        /// Получить баланс аккаунта
        /// </summary>
        Task<AccountBalance> GetBalanceAsync();

        /// <summary>
        /// Получить историю ордеров
        /// </summary>
        Task<List<Order>> GetOrderHistoryAsync(string symbol = null);
    }
}
