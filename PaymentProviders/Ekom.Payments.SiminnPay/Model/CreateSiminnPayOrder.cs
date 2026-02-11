namespace Ekom.Payments.SiminnPay.Model
{
    /// <summary>
    /// Pay order
    /// </summary>
    public class CreateSiminnPayOrder
    {
        /// <summary>
        /// Pay Transaction Id
        /// </summary>
        public string ReferenceId { get; set; }

        /// <summary>
        /// Order description, taken from order line.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// User phone number.
        /// </summary>
        public string PhoneNumber { get; set; }

        /// <summary>
        /// Total Amount
        /// </summary>
        public decimal Amount { get; set; }
    }
}
