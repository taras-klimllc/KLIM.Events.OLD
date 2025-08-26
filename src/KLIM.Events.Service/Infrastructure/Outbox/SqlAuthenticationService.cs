using Azure.Core;
using Azure.Identity;

namespace KLIM.Events.Service.Infrastructure.Outbox;

/// <summary>
/// Handles Azure AD authentication for SQL connections
/// </summary>
public sealed class SqlAuthenticationService
{
    private const string AZURE_SQL_SCOPE = "https://database.windows.net/.default";

    public async Task<string> AcquireTokenAsync(CancellationToken cancellationToken = default)
    {
        var credential = new DefaultAzureCredential();
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { AZURE_SQL_SCOPE }),
            cancellationToken);
        return token.Token;
    }

    public string SanitizeConnectionString(string connectionString, bool useAzureAd)
    {
        if (!useAzureAd) return connectionString;

        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        builder.Remove("User ID");
        builder.Remove("Password");
        return builder.ConnectionString;
    }
}