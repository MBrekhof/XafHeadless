namespace XafHeadless.JobServer.Services.Email;

// Ported from a companion headless implementation's email settings (READ-ONLY source). Username/Password/
// EnableSsl are kept for shape parity but stay empty/false in appsettings.json -- smtp4dev needs none
// of it (dev-only sink).
public class EmailSettings {
    public string SmtpServer { get; set; } = string.Empty;
    public int SmtpPort { get; set; }
    public bool EnableSsl { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public bool SendEmails { get; set; } = true;
}
