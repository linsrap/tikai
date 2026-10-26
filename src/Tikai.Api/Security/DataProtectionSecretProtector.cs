using Microsoft.AspNetCore.DataProtection;
using Tikai.Core.Security;

namespace Tikai.Api.Security;

/// <summary>Secrets encrypted with ASP.NET Core Data Protection, using the hub's key ring in the data volume.</summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public string Protect(string purpose, string plaintext) => provider.CreateProtector(purpose).Protect(plaintext);

    public string Unprotect(string purpose, string protectedValue) => provider.CreateProtector(purpose).Unprotect(protectedValue);
}
