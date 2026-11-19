namespace Ekom.Payments.Rapyd;

/// <summary>
/// Custom LoanType specification for Rapyd, allows Rapyd PP to approximate how Teya specifies their loans.
/// We map from these values to the correct Rapyd parameters inside Payment
/// </summary>
public enum LoanType
{
    /// <summary>
    /// </summary>
    Disabled,

    /// <summary>
    /// </summary>
    IsLoan,

    /// <summary>
    /// </summary>
    IsInterestFreeLoan,
}
