namespace Ekom.Payments.SiminnPay.Exceptions
{

    [Serializable]
    public class SiminnPayApiUnauthorizedException : Exception
    {
        public SiminnPayApiUnauthorizedException() { }
        public SiminnPayApiUnauthorizedException(string message) : base(message) { }
        public SiminnPayApiUnauthorizedException(string message, Exception inner) : base(message, inner) { }
        protected SiminnPayApiUnauthorizedException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
