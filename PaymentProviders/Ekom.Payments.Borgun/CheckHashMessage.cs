using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ekom.Payments.Borgun;
class CheckHashMessage
{
    public string Message;

    public CheckHashMessage(string orderId, string amount, string currency)
    {
        Message = orderId + "|" + amount + "|" + currency;
    }

    public CheckHashMessage(string merchantId, string returnUrlSuccess, string returnUrlSuccessServer,
                     string orderId, string amount, string currency)
    {
        Message = merchantId + "|" + returnUrlSuccess + "|" + returnUrlSuccessServer + "|" + orderId + "|"
                             + amount + "|" + currency;
    }
}
