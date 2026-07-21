namespace XafHeadless.JobServer.Services.Email;

// Ported from a companion headless implementation's email service interface (READ-ONLY source), trimmed to the
// one method EmailOrdersReportHandler calls -- YAGNI drops the string[]-recipients, IEnumerable-to,
// and file-path-attachment overloads the source carries; this host always sends one recipient with
// one in-memory PDF attachment.
public interface IEmailService {
    Task SendEmailWithAttachmentsAsync(string toEmail, string subject, string htmlBody, Dictionary<string, byte[]> attachments);
}
