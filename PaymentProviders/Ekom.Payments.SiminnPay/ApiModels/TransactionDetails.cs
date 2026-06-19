using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    [DataContract]
    public class TransactionDetails
    {
        [DataMember(Name = "orderKey")]
        public string? OrderKey { get; set; } = null;

        [DataMember(Name = "transactionId", EmitDefaultValue = false)]
        public string? TransactionId { get; set; } = null;

        [DataMember(Name = "amount")]
        public decimal Amount { get; set; }

        [DataMember(Name = "cardType", EmitDefaultValue = false)]
        public string? CardType { get; set; } = null;

        [DataMember(Name = "cardPaymentType", EmitDefaultValue = false)]
        public string? CardPaymentType { get; set; } = null;

        [DataMember(Name = "cardLast4", EmitDefaultValue = false)]
        public string? CardLast4 { get; set; } = null;

        [DataMember(Name = "acquirerAuthCode", EmitDefaultValue = false)]
        public string? AcquirerAuthCode { get; set; } = null;

        [DataMember(Name = "acquirerAcceptedDate")]
        public string? AcquirerAcceptedDate { get; set; } = null;

        [DataMember(Name = "acquirerName", EmitDefaultValue = false)]
        public string? AcquirerName { get; set; } = null;
    }
}
