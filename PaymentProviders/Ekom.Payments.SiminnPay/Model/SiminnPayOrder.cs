using Umbraco.Core.Persistence.DatabaseAnnotations;
using NPoco;

namespace Ekom.Payments.SiminnPay.Model
{
    /// <summary>
    /// Pay order as persisted in SQL
    /// </summary>
    [TableName("customSiminnPayOrder")]
    [PrimaryKey("OrderKey", AutoIncrement = false)]
    public class SiminnPayOrder
    {
        /// <summary>
        /// Siminn pay unique key for order
        /// </summary>
        [PrimaryKeyColumn(AutoIncrement = false)]
        public Guid OrderKey { get; set; }

        /// <summary>
        /// Pay Transaction Id
        /// </summary>
        [Length(50)]
        public string ReferenceId { get; set; }

        /// <summary>
        /// Order description, taken from order line.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// User phone number.
        /// </summary>
        [Length(50)]
        public string PhoneNumber { get; set; }

        public int DbStatus { get; set; }

        /// <summary>
        /// This is the state of the purchase. 
        /// Possible states are WAIT when the request is still pending, 
        /// FINISHED when the purchase is completed successfully 
        /// and CANCELLED when the purchase is cancelled either by the user or it times out.
        /// </summary>
        [ResultColumn]
        public SiminnPayStatus Status 
        {
            get => (SiminnPayStatus)DbStatus;
            set => DbStatus = (int)value;
        }

        /// <summary>
        /// Total Amount
        /// </summary>
        public decimal Amount { get; set; }
        /// <summary>
        /// Original amount this is only used if the price is discounted.
        /// </summary>
        public decimal OriginalAmount { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public DateTime Created { get; set; }

        /// <summary>
        /// 
        /// </summary>
        [NullSetting(NullSetting = NullSettings.Null)]
        public DateTime? Completed { get; set; }

        /// <summary>
        /// 
        /// </summary>
        [NullSetting(NullSetting = NullSettings.Null)]
        public DateTime? Expires { get; set; }

        /// <summary>
        /// String name of payment provider <see cref="IPublishedContent"/> node
        /// Helps to resolve overloaded payment providers, f.x. Borgun USD and Borgun ISK
        /// </summary>
        [Length(50)]
        public string PaymentProvider { get; set; }

        public Guid NetPaymentOrderId { get; set; }
    }
}
