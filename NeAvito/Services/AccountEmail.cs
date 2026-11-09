using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace NeAvito.Services;

public sealed class AccountEmailOptions
{
    public string PublicOrigin { get; set; } = "";
    public string From { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? PickupDirectory { get; set; }

    public bool IsValid(bool development) =>
        Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.AbsolutePath == "/"
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && MailAddress.TryCreate(From, out _)
        && (string.IsNullOrWhiteSpace(PickupDirectory)
            ? !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535
                && string.IsNullOrWhiteSpace(UserName) == string.IsNullOrWhiteSpace(Password)
            : development);
}

public interface IAccountEmail
{
    Task<bool> SendLinkAsync(string email, string route, string userId, string token, CancellationToken cancellation);
}

internal sealed class AccountEmail(
    IOptions<AccountEmailOptions> options, IWebHostEnvironment environment, ILogger<AccountEmail> logger) : IAccountEmail
{
    public async Task<bool> SendLinkAsync(
        string email, string route, string userId, string token, CancellationToken cancellation)
    {
        var config = options.Value;
        var query = QueryString.Create("userId", userId).Add("token", token);
        var link = config.PublicOrigin.TrimEnd('/') + "/#" + route + query;
        var subject = route == "confirm-email" ? "Подтверждение почты" : "Восстановление пароля";
        using var message = new MailMessage(config.From, email, subject + " — NeAvito",
            $"Откройте ссылку, чтобы продолжить:\n{link}\n\nЕсли вы не запрашивали это письмо, пропустите его.");
        using var smtp = new SmtpClient();
        try
        {
            if (!string.IsNullOrWhiteSpace(config.PickupDirectory))
            {
                smtp.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
                smtp.PickupDirectoryLocation = Path.GetFullPath(config.PickupDirectory, environment.ContentRootPath);
                Directory.CreateDirectory(smtp.PickupDirectoryLocation);
            }
            else
            {
                smtp.Host = config.Host;
                smtp.Port = config.Port;
                smtp.EnableSsl = true;
                if (!string.IsNullOrWhiteSpace(config.UserName))
                    smtp.Credentials = new NetworkCredential(config.UserName, config.Password);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await smtp.SendMailAsync(message, timeout.Token);
            return true;
        }
        catch (Exception error) when (error is SmtpException or IOException or UnauthorizedAccessException
            || error is OperationCanceledException && !cancellation.IsCancellationRequested)
        {
            logger.LogWarning("Account email delivery failed for {Route} ({ErrorType}).", route, error.GetType().Name);
            return false;
        }
    }
}
