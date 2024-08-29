using System.Net.Mail;

namespace Ekom.Payments;
public interface IMailService
{
    string From { get; set; }
    string To { get; set; }

    Task SendAsync(MailMessage mailMessage);
}
