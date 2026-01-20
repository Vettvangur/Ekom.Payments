namespace Ekom.Payments.SiminnPay.Exceptions
{

    [Serializable]
    public class SiminnPayApiUnknownPhoneException : Exception
    {
        public SiminnPayApiUnknownPhoneException() { }
        public SiminnPayApiUnknownPhoneException(string message) : base(message) { }
        public SiminnPayApiUnknownPhoneException(string message, Exception inner) : base(message, inner) { }
        protected SiminnPayApiUnknownPhoneException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
