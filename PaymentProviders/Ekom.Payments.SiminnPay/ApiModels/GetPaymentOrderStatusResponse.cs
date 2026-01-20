using Ekom.Payments.SiminnPay.Model;
using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    [DataContract]
    public class GetPaymentOrderStatusResponse
    {
        [DataMember(Name = "referenceId", EmitDefaultValue = false)]
        public string ReferenceId { get; set; }

        [DataMember(Name = "status")]
        public SiminnPayStatus Status { get; set; }

        [DataMember(Name = "transactionDetails", EmitDefaultValue = false)]
        public TransactionDetails TransactionDetails { get; set; }
    }
}
