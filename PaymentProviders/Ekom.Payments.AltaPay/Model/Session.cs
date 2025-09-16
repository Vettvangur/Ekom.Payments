namespace Ekom.Payments.AltaPay.Model;

public class Session
{
    public string SessionId { get; set; }
    public SessionOrder Order { get; set; }
    public SessionCallbacks Callbacks { get; set; }
    public SessionConfiguration Configuration { get; set; }
}

public class SessionOrder
{
    public string OrderId { get; set; }
    public SessionOrderAmount Amount { get; set; }
    public List<SessionOrderLine> OrderLines { get; set; }
}

public class SessionOrderAmount
{
    public double Value { get; set; }
    public string Currency { get; set; }
}

public class SessionOrderLine
{
    public string ItemId { get; set; }
    public string Description { get; set; }
    public int Quantity { get; set; }
    public double UnitPrice { get; set; }
}

public class SessionCallbacks
{
    public SessionCallback Success { get; set; }
    public SessionCallback Failure { get; set; }
    public string Redirect { get; set; }
    public string Notification { get; set; }
    public string BodyFormat { get; set; } = "JSON";
}

public class SessionCallback
{
    public string Type { get; set; } = "URL";
    public string Value { get; set; }
}

public class SessionConfiguration
{
    public string PaymentType { get; set; } = "PAYMENT";
    public string Language { get; set; }
}
