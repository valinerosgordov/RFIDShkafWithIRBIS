using System.Collections.Generic;

namespace TradingBot.Models
{
    /// <summary>
    /// Баланс аккаунта
    /// </summary>
    public class AccountBalance
    {
        public decimal TotalBalance { get; set; }
        public decimal AvailableBalance { get; set; }
        public decimal LockedBalance { get; set; }
        public Dictionary<string, decimal> Assets { get; set; }

        public AccountBalance()
        {
            Assets = new Dictionary<string, decimal>();
        }
    }
}
