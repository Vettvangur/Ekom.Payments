using Ekom.Payments.Netgiro;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ekom.Payments.Netgiro;

/// <summary>
/// Helper that facilitates easy addition of a prepopulated instance of your payment providers custom settings
/// prior to calling RequestAsync on f.x. Ekom.Payments.Netgiro.Payment
/// </summary>
public static class PaymentSettingsExtensions
{
    public static void SetNetgiroSettings(
        this PaymentSettings settings, 
        NetgiroSettings NetgiroSettings)
    {
        settings.CustomSettings[typeof(NetgiroSettings)] = NetgiroSettings;
    }
}
