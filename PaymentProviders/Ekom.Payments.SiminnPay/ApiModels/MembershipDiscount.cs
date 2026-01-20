using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    /// <summary>
    /// Membership <see cref="Discount"/>
    /// </summary>
    /// <seealso cref="Discount" />
    [DataContract]
    public class MembershipDiscount : Discount
    {
        /// <summary>
        /// The ID of the membership that triggered this discount.
        /// </summary>
        [DataMember]
        public string MembershipId { get; set; }
    }
}
