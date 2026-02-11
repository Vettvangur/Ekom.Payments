using Ekom.Payments.SiminnPay.Model;
using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    [DataContract]
    public class CreatePaymentOrderResponse
    {
        [DataMember(Name = "orderKey")]
        public Guid OrderKey { get; set; }

        [DataMember(Name = "referenceId")]
        public string ReferenceId { get; set; }

        [DataMember(Name = "status")]
        public SiminnPayStatus Status { get; set; }

        [DataMember(Name = "hasLoyalty")]
        public bool HasLoyalty { get; set; }

        [DataMember(Name = "memberships")]
        public List<MembershipInfo> Memberships { get; set; }

        [DataMember(Name = "offerIds")]
        public List<string> OfferIds { get; set; }
    }
}
