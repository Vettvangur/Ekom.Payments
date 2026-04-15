using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    /// <summary>
    /// Info about offer
    /// </summary>
    [DataContract]
    class OfferDiscount : Discount
    {
        /// <summary>
        /// The ID of the offer that triggered this discount.
        /// </summary>
        [DataMember(Name = "offerId", EmitDefaultValue = false)]
        public string OfferId { get; set; }
    }
}
