using HonestLicenseServer.Models;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace HonestLicenseServer.Infrastructure;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "spi@morkovka.tech";
    public bool EnableSsl { get; set; } = true;
}

public interface IConnectionRequestNotifier
{
    Task NotifyAsync(ConnectionRequest request, CancellationToken cancellationToken);
}

public sealed class EmailConnectionRequestNotifier(IOptions<SmtpOptions> options)
    : IConnectionRequestNotifier
{
    private readonly SmtpOptions _options = options.Value;

    public async Task NotifyAsync(ConnectionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Host) || string.IsNullOrWhiteSpace(_options.From) ||
            string.IsNullOrWhiteSpace(_options.To))
            throw new InvalidOperationException("SMTP is not configured.");

        using var message = new MailMessage(_options.From, _options.To)
        {
            Subject = $"Новая заявка HonestFlow — {SubjectPart(request.Company, request.ContactName)} — " +
                      $"{request.WorkplaceCount} рабочих мест",
            Body = BuildBody(request),
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_options.Username, _options.Password)
        };

        await client.SendMailAsync(message, cancellationToken);
    }

    private static string SubjectPart(string? company, string contactName) =>
        (string.IsNullOrWhiteSpace(company) ? contactName : company)
        .Replace('\r', ' ').Replace('\n', ' ');

    private static string BuildBody(ConnectionRequest request) => $$"""
        Новая заявка на подключение HonestFlow

        Контактное лицо: {{request.ContactName}}
        Компания: {{request.Company}}
        Телефон: {{request.Phone}}
        Email: {{request.Email}}
        Город: {{request.City}}
        Рабочих мест: {{request.WorkplaceCount}}
        Товароучётная система: {{request.InventorySystem}}

        Комментарий:
        {{request.Comment}}

        Дата: {{request.CreatedAtUtc:O}}
        IP: {{request.IpAddress}}
        User-Agent: {{request.UserAgent}}
        """;
}
