using System.Runtime.Serialization;

namespace Ekom.Payments.SiminnPay.Model
{
    [DataContract]
    public partial class SiminnPayLoanResponse
    {
        [DataMember(Name = "InterestRate")]
        public double InterestRate { get; set; }

        [DataMember(Name = "DisbursementAmount")]
        public long DisbursementAmount { get; set; }

        [DataMember(Name = "DisbursementFeePercentage")]
        public double DisbursementFeePercentage { get; set; }

        [DataMember(Name = "DisbursementFeeAmount")]
        public double DisbursementFeeAmount { get; set; }

        [DataMember(Name = "YearlyCostRatio")]
        public double YearlyCostRatio { get; set; }

        [DataMember(Name = "TotalPrincipalAmount")]
        public double TotalPrincipalAmount { get; set; }

        [DataMember(Name = "TotalInterestAmount")]
        public long TotalInterestAmount { get; set; }

        [DataMember(Name = "TotalFeeAmount")]
        public long TotalFeeAmount { get; set; }

        [DataMember(Name = "TotalRepayment")]
        public long TotalRepayment { get; set; }

        [DataMember(Name = "PaymentFeePerPayment")]
        public long PaymentFeePerPayment { get; set; }

        [DataMember(Name = "AveragePaymentAmount")]
        public long AveragePaymentAmount { get; set; }

        [DataMember(Name = "LoanLengthInMonths")]
        public long LoanLengthInMonths { get; set; }

        /// <summary>
        /// Array containing other possible loan lengths.
        /// Lengths not listed indicate options are not available due to either
        /// invalid ÁHK or not one of the default length options defined by Siminn Pay.
        /// The default lengths Siminn Pay supports are as of writing:
        /// "Við bjóðum upp á 2-3-6-9-12-18-24-36 mánuði, en það getur breyst."
        /// </summary>
        [DataMember(Name = "AvailableLoanOptions", EmitDefaultValue = false)]
        public IEnumerable<SiminnPayLoanResponse> AvailableLoanOptions { get; set; }
    }
}
