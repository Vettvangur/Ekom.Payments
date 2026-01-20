using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.apimodels
{
    [DataContract]
    public class MembershipInfo
    {
        [DataMember(Name = "externalMembershipClubId", EmitDefaultValue = false)]
        public string ExternalMembershipClubId { get; set; }

        [DataMember(Name = "externalMembershipId", EmitDefaultValue = false)]
        public string ExternalMembershipId { get; set; }
    }
}
