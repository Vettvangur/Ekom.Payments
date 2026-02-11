using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    [DataContract]
    class CreatePaymentOrderRequest
    {
        [DataMember(Name = "referenceId", EmitDefaultValue = false)]
        public string ReferenceId { get; set; }

        [DataMember(Name = "amount")]
        public decimal Amount { get; set; }

        [DataMember(Name = "currency", EmitDefaultValue = false)]
        public string Currency { get; set; }

        [DataMember(Name = "description", EmitDefaultValue = false)]
        public string Description { get; set; }

        [DataMember(Name = "recipients", EmitDefaultValue = false)]
        public IEnumerable<Recipient> Recipients { get; set; }

        [DataMember(Name = "restrictToLoan")]
        public bool RestrictToLoan { get; set; }

        [DataMember(Name = "paymentType", EmitDefaultValue = false)]
        public string PaymentType { get; set; }

        [DataMember(Name = "allowUnregisteredRecipients")]
        public bool AllowUnregisteredRecipients { get; set; }

        [DataMember(Name = "timeToLive")]
        public int TimeToLive { get; set; }

        [DataMember(Name = "callbackUrl", EmitDefaultValue = false)]
        public string CallbackUrl { get; set; }

        [DataMember(Name = "appCallbackUrl", EmitDefaultValue = false)]
        public string AppCallbackUrl { get; set; }
    }
}
