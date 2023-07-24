using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ekom.Payments.Netgiro;

/// <summary>
/// https://netgiro.github.io/checkout.html
/// </summary>
public enum ConfirmationType
{
    /// <summary>
    /// If ConfirmationType = Automatic => Cart is confirmed automatically on server and provider just calls CheckCart periodically to check status of cart
    /// </summary>
    Automatic,

    /// <summary>
    /// If ConfirmationType = Manual => Provider calls ConfirmCart
    /// </summary>
    ServerCallback,

    /// <summary>
    /// If ConfirmationType = ServerCallback => Provider gets callback from server that cart is confirmed
    /// </summary>
    Manual,
}
