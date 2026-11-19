using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ekom.Payments.Rapyd;

[Serializable]
public class RapydAHKException : Exception
{
    public RapydAHKException() { }
    public RapydAHKException(string message) : base(message) { }
    public RapydAHKException(string message, Exception inner) : base(message, inner) { }
    protected RapydAHKException(
      System.Runtime.Serialization.SerializationInfo info,
      System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
}
