using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.ExpressApp.Security.Authentication.ClientServer;
using Microsoft.AspNetCore.Mvc;

namespace XafHeadless.Api.Auth;

// JWT authentication endpoint (POST api/Authentication/Authenticate). Copied from a production
// XAF (Blazor Server) app; Swagger annotations dropped since this headless host has no Swagger UI.
[ApiController]
[Route("api/[controller]")]
public class AuthenticationController : ControllerBase {
    readonly IAuthenticationTokenProvider tokenProvider;
    public AuthenticationController(IAuthenticationTokenProvider tokenProvider)
        => this.tokenProvider = tokenProvider;

    [HttpPost("Authenticate")]
    public IActionResult Authenticate([FromBody] AuthenticationStandardLogonParameters logonParameters) {
        try {
            return Ok(tokenProvider.Authenticate(logonParameters));
        }
        catch (AuthenticationException ex) {
            return Unauthorized(ex.GetJson());
        }
    }
}
