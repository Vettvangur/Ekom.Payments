namespace Ekom.Payments.SiminnPay.Exceptions
{

    [Serializable]
    public class SiminnPayApiResponseException : Exception
    {
        public SiminnPayApiResponseException() { }
        public SiminnPayApiResponseException(string message) : base(message) { }
        public SiminnPayApiResponseException(string message, Exception inner) : base(message, inner) { }
        protected SiminnPayApiResponseException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
