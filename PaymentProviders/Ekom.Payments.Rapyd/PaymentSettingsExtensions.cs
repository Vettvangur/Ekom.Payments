using Ekom.Payments.Rapyd;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ekom.Payments.Rapyd;

public static class PaymentSettingsExtensions
{
    public static void SetRapydSettings(
        this PaymentSettings settings, 
        RapydSettings rapydSettings)
    {
        settings.CustomSettings[typeof(RapydSettings)] = rapydSettings;
    }
}
