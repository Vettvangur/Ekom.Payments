namespace Ekom.Payments.SiminnPay.Exceptions
{
    [Serializable]
    public class SiminnPayApiUnprocessableEntityException : Exception
    {
        public SiminnPayApiUnprocessableEntityException() { }
        public SiminnPayApiUnprocessableEntityException(string message) : base(message) { }
        public SiminnPayApiUnprocessableEntityException(string message, Exception inner) : base(message, inner) { }
        protected SiminnPayApiUnprocessableEntityException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
