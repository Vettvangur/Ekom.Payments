namespace Ekom.Payments.SiminnPay.Exceptions
{
    [Serializable]
    public class SiminnPayApiNotFoundException : Exception
    {
        public SiminnPayApiNotFoundException() { }
        public SiminnPayApiNotFoundException(string message) : base(message) { }
        public SiminnPayApiNotFoundException(string message, Exception inner) : base(message, inner) { }
        protected SiminnPayApiNotFoundException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
