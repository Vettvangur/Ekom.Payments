using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    /// <summary>
    /// Payment order update amount request
    /// </summary>
    [DataContract]
    class UpdateOrderAmountRequest
    {

        /// <summary>
        /// The original amount before the discount was 
        /// calculated.
        /// </summary>
        [DataMember(Name = "originalAmount")]
        public decimal OriginalAmount { get; set; }

        /// <summary>
        /// The amount after the discount has
        /// been taken into account.
        /// </summary>
        [DataMember(Name = "newAmount")]
        public decimal NewAmount { get; set; }

        /// <summary>
        /// The discount applied due to offers.
        /// </summary>
        [DataMember(Name = "offerDiscounts", EmitDefaultValue = false)]
        public IEnumerable<OfferDiscount> OfferDiscounts { get; set; }

        /// <summary>
        /// The discounts applied due to membership.
        /// </summary>
        [DataMember(Name = "membershipDiscounts", EmitDefaultValue = false)]
        public IEnumerable<MembershipDiscount> MembershipDiscounts { get; set; }
    }
}
