using Ekom.Payments.Borgun;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ekom.Payments.Borgun;

/// <summary>
/// Helper that facilitates easy addition of a prepopulated instance of your payment providers custom settings
/// prior to calling RequestAsync on f.x. Ekom.Payments.Borgun.Payment
/// </summary>
public static class PaymentSettingsExtensions
{
    public static void SetBorgunSettings(
        this PaymentSettings settings, 
        BorgunSettings BorgunSettings)
    {
        settings.CustomSettings[typeof(BorgunSettings)] = BorgunSettings;
    }
}
