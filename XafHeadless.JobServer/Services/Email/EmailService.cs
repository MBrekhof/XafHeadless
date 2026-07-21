using DevExpress.ExpressApp;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using XafHeadless.JobServer.BusinessObjects;

namespace XafHeadless.JobServer.Services.Email;

// Ported from a companion headless implementation's email service (READ-ONLY source), trimmed to the one
// method EmailOrdersReportHandler calls. ArchiveEmail is retargeted to THIS host's EmailArchive shape
// (SentUtc/From/To/Subject/Success/ErrorMessage) and written via INonSecuredObjectSpaceFactory in the
// OUTER job scope -- same non-secured-write pattern XafJobExecutionRecorder/ReportArtifact already use
// (TenantId stays null there, so it hits the writable host branch; see docs/DEVIATIONS.md).
//
// I-1 (at-most-once email under Hangfire retry -- SVR-001 Dispatch D review carry-forward, REQUIRED
// here): the source's ArchiveEmail runs in a `finally` block, so an archive-write failure AFTER a
// successful SMTP send would throw back out of this method. JobExecutor's catch would then record a
// false Failure and rethrow, and Hangfire's [AutomaticRetry(Attempts=3)] would re-run the handler --
// re-sending mail that already went out. Fixed here: on SMTP success, ArchiveEmail runs best-effort
// (logged, never rethrown) so a failed audit write can never turn an already-sent email into a retry
// trigger. On SMTP failure, ArchiveEmail is still best-effort (must not mask the real SMTP exception
// with an archive exception), and the original SMTP failure is rethrown -- retrying a pre-send failure
// is safe and correct, since no mail went out.
public sealed class EmailService(
    IOptions<EmailSettings> emailSettings,
    ILogger<EmailService> logger,
    INonSecuredObjectSpaceFactory objectSpaceFactory) : IEmailService {
    readonly EmailSettings _settings = emailSettings.Value;

    public async Task SendEmailWithAttachmentsAsync(string toEmail, string subject, string htmlBody, Dictionary<string, byte[]> attachments) {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
        message.To.Add(new MailboxAddress(string.Empty, toEmail));
        message.Subject = subject;

        var bodyBuilder = new BodyBuilder { HtmlBody = htmlBody };
        foreach (var attachment in attachments)
            bodyBuilder.Attachments.Add(attachment.Key, attachment.Value);
        message.Body = bodyBuilder.ToMessageBody();

        try {
            await SendMessageAsync(message);
            logger.LogInformation("Email sent successfully to {ToEmail} with subject: {Subject}", toEmail, subject);
        }
        catch (Exception ex) {
            logger.LogError(ex, "Failed to send email to {ToEmail} with subject: {Subject}", toEmail, subject);
            ArchiveEmail(toEmail, subject, success: false, ex.Message);
            throw; // pre-send failure: no mail went out, so a Hangfire retry is safe and correct.
        }

        // SMTP succeeded. Past this point nothing may throw back into JobExecutor's retry path (I-1).
        ArchiveEmail(toEmail, subject, success: true, errorMessage: null);
    }

    async Task SendMessageAsync(MimeMessage message) {
        if (!_settings.SendEmails) {
            logger.LogInformation("Email sending is disabled in configuration. Email to {Recipients} with subject '{Subject}' was not sent.",
                string.Join(", ", message.To.Select(t => t.ToString())), message.Subject);
            return;
        }

        using var client = new SmtpClient();
        client.Timeout = _settings.TimeoutSeconds * 1000;

        var secureSocketOptions = _settings.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(_settings.SmtpServer, _settings.SmtpPort, secureSocketOptions);
        try {
            if (!string.IsNullOrEmpty(_settings.Username) && !string.IsNullOrEmpty(_settings.Password))
                await client.AuthenticateAsync(_settings.Username, _settings.Password);
            await client.SendAsync(message);
        }
        finally {
            if (client.IsConnected) await client.DisconnectAsync(true);
        }
    }

    // Best-effort by design (I-1): logs but never throws, so a failed audit write can neither mask a
    // real SMTP failure nor -- on the success path -- turn an already-sent email into a retry trigger.
    void ArchiveEmail(string to, string subject, bool success, string? errorMessage) {
        try {
            using var os = objectSpaceFactory.CreateNonSecuredObjectSpace<EmailArchive>();
            var archive = os.CreateObject<EmailArchive>();
            archive.SentUtc = DateTime.UtcNow;
            archive.From = _settings.FromEmail;
            archive.To = to;
            archive.Subject = subject;
            archive.Success = success;
            archive.ErrorMessage = errorMessage;
            os.CommitChanges();
        }
        catch (Exception ex) {
            logger.LogError(ex, "Failed to archive email record for {ToEmail} (send success={Success})", to, success);
        }
    }
}
