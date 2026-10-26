namespace Tikai.Api.Auth;

public sealed record CurrentUser(Guid Id, string Email, string DisplayName, bool IsAdmin);

public sealed record AuthStatus(bool SignUpOpen, CurrentUser? User);

public sealed record SignUpRequest(string Email, string DisplayName, string Password);

public sealed record LoginRequest(string Email, string Password);
