namespace Ekom.Payments.AltaPay.Model;

public class AltaSettings : PaymentSettingsBase<AltaSettings>
{
    public string ApiUserName { get; set; }

    public string ApiPassword { get; set; }

    public string Terminal { get; set; }

    public Uri BaseAddress { get; set; }

    public Uri AuthenticationUrl { get; set; }

    public Uri SessionUrl { get; set; }

    public string? SessionId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public AltaPaymentConfig PaymentConfig => new AltaPaymentConfig
    {
        UserName = ApiUserName,
        Password = ApiPassword,
        Terminal = Terminal,
        BaseAddress = BaseAddress,
        AuthenticationUrl = AuthenticationUrl,
        SessionUrl = SessionUrl
    };
}

public class AltaPaymentConfig {
    public string UserName { get; set; }
    public string Password { get; set; }
    public string Terminal { get; set; }
    public Uri BaseAddress { get; set; }
    public Uri AuthenticationUrl { get; set; }
    public Uri SessionUrl { get; set; }
}
