namespace HybridConnectionClientProxy.Settings;

public class AppSettings
{
	public const string Section = "AppSettings";

	public Proxy[] Proxies { get; set; } = [];

	/// <summary>
	/// Entra ID sign-in, used by proxies whose connection string has no shared access key.
	/// </summary>
	public EntraId? EntraId { get; set; }
}
