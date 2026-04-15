using Ekom.Payments.SiminnPay.apimodels;
using System.ComponentModel.DataAnnotations;

namespace Ekom.Payments.SiminnPay.Model
{
    public class SiminnPayOrderStatus
    {
        /// <summary> The order key </summary>
        [Required]
        public Guid OrderKey { get; set; }

        /// <summary> Amount of order </summary>
        [Required]
        public decimal Amount { get; set; }

        /// <summary> Status of order - 0 if paid </summary>
        [Required]
        public SiminnPayStatus Status { get; set; }

        /// <summary> Expiration date and time of order </summary>
        [Required]
        public DateTime ExpiresAt { get; set; }

        /// <summary> Hash value of OrderKey + Amount + ExipresAt </summary>
        [Required]
        public string HMAC { get; set; }

        /// <summary> Details of transaction ip paid </summary>
        public TransactionDetails TransactionDetails { get; set; }
    }
}
