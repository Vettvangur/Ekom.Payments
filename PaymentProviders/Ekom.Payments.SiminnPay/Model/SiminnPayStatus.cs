namespace Ekom.Payments.SiminnPay.Model
{
    /// <summary>
    /// This is the state of the purchase. 
    /// </summary>
    public enum SiminnPayStatus
    {
        /// <summary>
        /// Payment done
        /// </summary>
        PaymentSuccessful = 0,
        /// <summary>
        /// Waiting for an updated amount
        /// </summary>
        WaitingForNewAmount = 121,
        /// <summary>
        /// Created and Waiting
        /// </summary>
        WaitingForCustomer = 201,
        /// <summary>
        /// Accepted waiting for payment
        /// </summary>
        CustomerAcceptedWaitingForConfirm = 211,
        /// <summary>
        /// Waiting for payment to be accepted
        /// </summary>
        WaitingForAcquirer = 301,
        /// <summary>
        /// Cancelled by Us
        /// </summary>
        CancelledByMerchant = 102,
        /// <summary>
        /// Cancelled by Customer
        /// </summary>
        CancelledByCustomer = 202,
        /// <summary>
        /// Expired and should be stopped
        /// </summary>
        Expired = 402,
        /// <summary>
        /// Payment Rejected
        /// </summary>
        AcquirerError = 303,
        UnknownError = 4
    }
}
