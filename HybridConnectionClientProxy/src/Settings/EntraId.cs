namespace HybridConnectionClientProxy.Settings;

/// <summary>
/// How the proxy signs in to Azure Relay with Microsoft Entra ID, for proxies whose
/// <see cref="Proxy.HybridConnectionString"/> carries no shared access key.
/// </summary>
public class EntraId
{
	public const string Section = "EntraId";

	public EntraIdMethod? Method { get; set; }

	/// <summary>
	/// The directory (tenant) id. Workload identity and Azure CLI only; workload identity defaults to <c>AZURE_TENANT_ID</c>.
	/// </summary>
	public string? TenantId { get; set; }

	/// <summary>
	/// The application (client) id: a user-assigned managed identity's, or the identity a workload identity signs in as
	/// (defaults to <c>AZURE_CLIENT_ID</c>). Leave out for a system-assigned managed identity.
	/// </summary>
	public string? ClientId { get; set; }

	/// <summary>
	/// Workload identity only: the file holding the federated token (defaults to <c>AZURE_FEDERATED_TOKEN_FILE</c>).
	/// </summary>
	public string? TokenFile { get; set; }
}

public enum EntraIdMethod
{
	/// <summary>An Azure managed identity: system-assigned, or user-assigned when <see cref="EntraId.ClientId"/> is set.</summary>
	ManagedIdentity,

	/// <summary>A federated token read from a file and exchanged for an Entra token (Kubernetes, or an OIDC issuer of your own).</summary>
	WorkloadIdentity,

	/// <summary>The account the Azure CLI is signed in as (<c>az login</c>, or <c>azure/login</c> in GitHub Actions).</summary>
	AzureCli,
}
