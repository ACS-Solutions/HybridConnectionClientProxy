using Azure.Core;
using Azure.Identity;

using HybridConnectionClientProxy.Settings;

using Microsoft.Azure.Relay;

namespace HybridConnectionClientProxy;

/// <summary>
/// Builds the relay connection provider for each proxy, choosing how it authenticates to Azure Relay.
/// </summary>
/// <remarks>
/// A connection string with a shared access key (or signature) signs in with it, as before. One without signs in
/// with Microsoft Entra ID, as set by <see cref="EntraId"/>: the identity needs the Azure Relay Sender role on the
/// hybrid connection (or its namespace). Every Entra proxy shares one credential, so one token serves them all.
/// </remarks>
internal sealed class HybridConnectionProviderFactory( EntraId? entraId, Func<EntraId, TokenCredential>? credentialFactory = null )
{
	private readonly Func<EntraId, TokenCredential> _credentialFactory = credentialFactory ?? CreateCredential;
	private TokenProvider? _entraTokenProvider;

	/// <summary>
	/// A factory for shared access key connection strings only.
	/// </summary>
	public static HybridConnectionProviderFactory SharedAccessKeyOnly { get; } = new( null );

	public IHybridConnectionProvider Create( Proxy proxy, string proxyLabel )
		=> new HybridConnectionClientProvider( CreateClient( proxy, proxyLabel ) );

	private HybridConnectionClient CreateClient( Proxy proxy, string proxyLabel )
	{
		if( string.IsNullOrWhiteSpace( proxy.HybridConnectionString ) )
			throw new ConfigurationException( $"Proxy {proxyLabel}: HybridConnectionString is required." );

		RelayConnectionStringBuilder builder;
		try
		{
			builder = new RelayConnectionStringBuilder( proxy.HybridConnectionString );
		}
		catch( Exception ex ) when( ex is ArgumentException or FormatException or UriFormatException )
		{
			throw new ConfigurationException(
				$"Proxy {proxyLabel}: HybridConnectionString is not a valid Azure Relay connection string ({SecretRedactor.Describe( ex )})."
			);
		}

		// No key, key name or signature at all means Entra ID; any of them means a shared access key, which must be complete.
		var useEntraId = string.IsNullOrEmpty( builder.SharedAccessSignature )
			&& string.IsNullOrEmpty( builder.SharedAccessKeyName )
			&& string.IsNullOrEmpty( builder.SharedAccessKey );

		var missing = new List<string>();
		if( builder.Endpoint is null )
			missing.Add( "Endpoint" );
		if( string.IsNullOrEmpty( builder.EntityPath ) )
			missing.Add( "EntityPath" );
		if( !useEntraId && string.IsNullOrEmpty( builder.SharedAccessSignature ) )
		{
			if( string.IsNullOrEmpty( builder.SharedAccessKeyName ) )
				missing.Add( "SharedAccessKeyName" );
			if( string.IsNullOrEmpty( builder.SharedAccessKey ) )
				missing.Add( "SharedAccessKey" );
		}

		if( missing.Count > 0 )
			throw new ConfigurationException(
				$"Proxy {proxyLabel}: HybridConnectionString is missing {string.Join( ", ", missing )}."
			);

		try
		{
			if( !useEntraId )
				return new HybridConnectionClient( proxy.HybridConnectionString );

			var address = new Uri( builder.Endpoint!, builder.EntityPath );
			return new HybridConnectionClient( address, GetEntraTokenProvider( proxyLabel ) )
			{
				OperationTimeout = builder.OperationTimeout,
			};
		}
		catch( Exception ex ) when( ex is ArgumentException or FormatException or UriFormatException )
		{
			throw new ConfigurationException(
				$"Proxy {proxyLabel}: HybridConnectionString was rejected ({SecretRedactor.Describe( ex )})."
			);
		}
	}

	private TokenProvider GetEntraTokenProvider( string proxyLabel )
	{
		if( _entraTokenProvider is not null )
			return _entraTokenProvider;

		if( entraId?.Method is null )
			throw new ConfigurationException(
				$"Proxy {proxyLabel}: HybridConnectionString has no shared access key, so it signs in with Entra ID, " +
				$"which needs {AppSettings.Section}:{EntraId.Section}:{nameof( EntraId.Method )} " +
				$"({string.Join( ", ", Enum.GetNames<EntraIdMethod>() )})."
			);

		_entraTokenProvider = new EntraTokenProvider( _credentialFactory( entraId ) ).ToRelayTokenProvider();
		return _entraTokenProvider;
	}

	internal static TokenCredential CreateCredential( EntraId entraId )
	{
		switch( entraId.Method )
		{
			case EntraIdMethod.ManagedIdentity:
				if( !string.IsNullOrEmpty( entraId.TenantId ) || !string.IsNullOrEmpty( entraId.TokenFile ) )
					throw new ConfigurationException(
						$"{EntraId.Section}: {nameof( EntraId.TenantId )} and {nameof( EntraId.TokenFile )} don't apply to {EntraIdMethod.ManagedIdentity}."
					);
				return new ManagedIdentityCredential(
					string.IsNullOrEmpty( entraId.ClientId )
						? ManagedIdentityId.SystemAssigned
						: ManagedIdentityId.FromUserAssignedClientId( entraId.ClientId )
				);

			case EntraIdMethod.WorkloadIdentity:
				// Unset values come from AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_FEDERATED_TOKEN_FILE.
				var options = new WorkloadIdentityCredentialOptions();
				if( !string.IsNullOrEmpty( entraId.TenantId ) )
					options.TenantId = entraId.TenantId;
				if( !string.IsNullOrEmpty( entraId.ClientId ) )
					options.ClientId = entraId.ClientId;
				if( !string.IsNullOrEmpty( entraId.TokenFile ) )
					options.TokenFilePath = entraId.TokenFile;

				var unset = new List<string>();
				if( string.IsNullOrEmpty( options.TenantId ) )
					unset.Add( $"{nameof( EntraId.TenantId )} (or AZURE_TENANT_ID)" );
				if( string.IsNullOrEmpty( options.ClientId ) )
					unset.Add( $"{nameof( EntraId.ClientId )} (or AZURE_CLIENT_ID)" );
				if( string.IsNullOrEmpty( options.TokenFilePath ) )
					unset.Add( $"{nameof( EntraId.TokenFile )} (or AZURE_FEDERATED_TOKEN_FILE)" );
				if( unset.Count > 0 )
					throw new ConfigurationException(
						$"{EntraId.Section}: {EntraIdMethod.WorkloadIdentity} needs {string.Join( ", ", unset )}."
					);

				return new WorkloadIdentityCredential( options );

			case EntraIdMethod.AzureCli:
				if( !string.IsNullOrEmpty( entraId.ClientId ) || !string.IsNullOrEmpty( entraId.TokenFile ) )
					throw new ConfigurationException(
						$"{EntraId.Section}: {nameof( EntraId.ClientId )} and {nameof( EntraId.TokenFile )} don't apply to {EntraIdMethod.AzureCli}."
					);
				return new AzureCliCredential( new AzureCliCredentialOptions { TenantId = string.IsNullOrEmpty( entraId.TenantId ) ? null : entraId.TenantId } );

			default:
				throw new ConfigurationException( $"{EntraId.Section}: unknown {nameof( EntraId.Method )} '{entraId.Method}'." );
		}
	}
}
