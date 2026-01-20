using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    [DataContract]
    public class Discount
    {
        /// <summary>
        /// The type of discount applied.
        /// </summary>
        [DataMember(Name = "discountType", EmitDefaultValue = false)]
        public string DiscountType { get; set; }

        /// <summary>
        /// The value of the discount applied.
        /// </summary>
        [DataMember(Name = "discountValue", EmitDefaultValue = false)]
        public string DiscountValue { get; set; }

        /// <summary>
        /// A description of the discount.
        /// </summary>
        [DataMember(Name = "discountText", EmitDefaultValue = false)]
        public string DiscountText { get; set; }
    }
}
