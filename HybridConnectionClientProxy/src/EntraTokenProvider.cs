using Azure.Core;

using Microsoft.Azure.Relay;

namespace HybridConnectionClientProxy;

/// <summary>
/// Gives Azure Relay Entra ID access tokens from any <see cref="TokenCredential"/>. Tokens are reused until
/// shortly before they expire, so a credential that does not cache (the Azure CLI's) isn't asked on every connection.
/// </summary>
internal sealed class EntraTokenProvider( TokenCredential credential, TimeProvider? timeProvider = null )
{
	internal static readonly string[] Scopes = [ "https://relay.azure.net/.default" ];

	/// <summary>
	/// A token this close to expiry is renewed rather than reused.
	/// </summary>
	internal static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes( 5 );

	// The relay SDK passes the authority to the callback and nothing else; the credential decides where it signs in.
	private const string Authority = "https://login.microsoftonline.com/organizations";

	private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
	private readonly SemaphoreSlim _lock = new( 1, 1 );
	private AccessToken? _cached;

	/// <summary>
	/// A relay <see cref="TokenProvider"/> backed by this provider, for <see cref="HybridConnectionClient"/>.
	/// </summary>
	public TokenProvider ToRelayTokenProvider()
		=> TokenProvider.CreateAzureActiveDirectoryTokenProvider(
			async ( _, _, _ ) => ( await GetAccessTokenAsync( CancellationToken.None ).ConfigureAwait( false ) ).Token,
			Authority,
			null
		);

	internal async Task<AccessToken> GetAccessTokenAsync( CancellationToken cancellationToken )
	{
		await _lock.WaitAsync( cancellationToken ).ConfigureAwait( false );
		try
		{
			if( _cached is { } cached && cached.ExpiresOn - _time.GetUtcNow() > RenewBefore )
				return cached;

			var token = await credential.GetTokenAsync( new TokenRequestContext( Scopes ), cancellationToken ).ConfigureAwait( false );
			_cached = token;
			return token;
		}
		finally
		{
			_lock.Release();
		}
	}
}
