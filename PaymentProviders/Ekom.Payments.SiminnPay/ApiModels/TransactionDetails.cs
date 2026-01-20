using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    [DataContract]
    public class TransactionDetails
    {
        [DataMember(Name = "orderKey")]
        public string OrderKey { get; set; }

        [DataMember(Name = "transactionId", EmitDefaultValue = false)]
        public string TransactionId { get; set; }

        [DataMember(Name = "amount")]
        public decimal Amount { get; set; }

        [DataMember(Name = "cardType", EmitDefaultValue = false)]
        public string CardType { get; set; }

        [DataMember(Name = "cardPaymentType", EmitDefaultValue = false)]
        public string CardPaymentType { get; set; }

        [DataMember(Name = "cardLast4", EmitDefaultValue = false)]
        public string CardLast4 { get; set; }

        [DataMember(Name = "acquirerAuthCode", EmitDefaultValue = false)]
        public string AcquirerAuthCode { get; set; }

        [DataMember(Name = "acquirerAcceptedDate")]
        public string AcquirerAcceptedDate { get; set; }

        [DataMember(Name = "acquirerName", EmitDefaultValue = false)]
        public string AcquirerName { get; set; }
    }
}
