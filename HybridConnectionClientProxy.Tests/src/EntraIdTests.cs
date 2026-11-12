using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Azure.Core;
using Azure.Identity;

using HybridConnectionClientProxy.Settings;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace HybridConnectionClientProxy.Tests;

/// <summary>
/// Entra ID sign-in (#15): connection strings without a shared access key, and the EntraId settings. No Azure needed.
/// </summary>
public class EntraIdTests
{
	private const string KeylessConnectionString = "Endpoint=sb://ns.servicebus.windows.net/;EntityPath=lonsql07dev-sql";
	private const string Key = "c2VjcmV0LWtleS12YWx1ZQ==";

	// -------------------------------------------------------------------------
	// Choosing shared access key or Entra ID
	// -------------------------------------------------------------------------

	[Fact]
	public void Create_KeylessWithoutEntraId_IsConfigurationErrorNamingTheSetting()
	{
		var ex = Assert.Throws<ConfigurationException>( () =>
			HybridConnectionProviderFactory.SharedAccessKeyOnly.Create( new Proxy { HybridConnectionString = KeylessConnectionString }, "#0" ) );

		Assert.Contains( "AppSettings:EntraId:Method", ex.Message );
	}

	[Fact]
	public void Create_KeylessWithEntraId_AddressesTheHybridConnection()
	{
		var factory = new HybridConnectionProviderFactory( new EntraId { Method = EntraIdMethod.AzureCli }, _ => new FakeCredential() );

		var provider = Assert.IsType<HybridConnectionClientProvider>(
			factory.Create( new Proxy { HybridConnectionString = KeylessConnectionString }, "#0" ) );

		Assert.Equal( new Uri( "sb://ns.servicebus.windows.net/lonsql07dev-sql" ), provider.Address );
	}

	[Fact]
	public void Create_KeylessProxies_ShareOneCredential()
	{
		var created = 0;
		var factory = new HybridConnectionProviderFactory(
			new EntraId { Method = EntraIdMethod.AzureCli },
			_ => { created++; return new FakeCredential(); } );

		factory.Create( new Proxy { HybridConnectionString = KeylessConnectionString }, "#0" );
		factory.Create( new Proxy { HybridConnectionString = "Endpoint=sb://ns.servicebus.windows.net/;EntityPath=loniridium02" }, "#1" );

		Assert.Equal( 1, created );
	}

	[Fact]
	public void Create_SharedAccessKey_IgnoresEntraId()
	{
		var factory = new HybridConnectionProviderFactory(
			new EntraId { Method = EntraIdMethod.AzureCli },
			_ => throw new InvalidOperationException( "No credential should be made for a shared access key." ) );

		factory.Create(
			new Proxy { HybridConnectionString = "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=cody;SharedAccessKey=" + Key + ";EntityPath=e" },
			"#0" );
	}

	[Theory]
	[InlineData( "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=cody;EntityPath=e", "SharedAccessKey" )]
	[InlineData( "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKey=" + Key + ";EntityPath=e", "SharedAccessKeyName" )]
	[InlineData( "Endpoint=sb://ns.servicebus.windows.net/", "EntityPath" )]
	public void Create_IncompleteString_IsStillAnErrorWithEntraId( string connectionString, string expectedInMessage )
	{
		var factory = new HybridConnectionProviderFactory( new EntraId { Method = EntraIdMethod.AzureCli }, _ => new FakeCredential() );

		var ex = Assert.Throws<ConfigurationException>( () =>
			factory.Create( new Proxy { HybridConnectionString = connectionString }, "#0" ) );

		Assert.Contains( expectedInMessage, ex.Message );
		Assert.DoesNotContain( Key, ex.Message );
	}

	[Fact]
	public async Task Start_KeylessWithEntraId_ListensWithoutSigningIn()
	{
		var credential = new FakeCredential();
		var port = FreePort();
		var settings = new AppSettings
		{
			Proxies = [ new Proxy { HybridConnectionString = KeylessConnectionString, ListenPort = port } ],
			EntraId = new EntraId { Method = EntraIdMethod.AzureCli },
		};

		var host = ProxyHost.Start( settings, new HybridConnectionProviderFactory( settings.EntraId, _ => credential ).Create );

		Assert.Equal( port, Assert.Single( host.ListenEndpoints ).Port );
		Assert.Equal( 0, credential.Calls );
		Assert.True( await host.StopAsync( TimeSpan.FromSeconds( 5 ) ) );
	}

	// -------------------------------------------------------------------------
	// Credentials
	// -------------------------------------------------------------------------

	[Fact]
	public void CreateCredential_UserAssignedManagedIdentity_IsManagedIdentityCredential()
		=> Assert.IsType<ManagedIdentityCredential>( HybridConnectionProviderFactory.CreateCredential(
			new EntraId { Method = EntraIdMethod.ManagedIdentity, ClientId = "00000000-0000-0000-0000-000000000001" } ) );

	[Fact]
	public void CreateCredential_WorkloadIdentity_IsWorkloadIdentityCredential()
		=> Assert.IsType<WorkloadIdentityCredential>( HybridConnectionProviderFactory.CreateCredential(
			new EntraId
			{
				Method = EntraIdMethod.WorkloadIdentity,
				TenantId = "00000000-0000-0000-0000-000000000002",
				ClientId = "00000000-0000-0000-0000-000000000001",
				TokenFile = "/run/token",
			} ) );

	[Fact]
	public void CreateCredential_AzureCli_IsAzureCliCredential()
		=> Assert.IsType<AzureCliCredential>( HybridConnectionProviderFactory.CreateCredential( new EntraId { Method = EntraIdMethod.AzureCli } ) );

	[Theory]
	[InlineData( EntraIdMethod.ManagedIdentity, null, null, "/run/token" )]
	[InlineData( EntraIdMethod.ManagedIdentity, "00000000-0000-0000-0000-000000000002", null, null )]
	[InlineData( EntraIdMethod.AzureCli, null, "00000000-0000-0000-0000-000000000001", null )]
	public void CreateCredential_SettingThatDoesNotApply_IsConfigurationError( EntraIdMethod method, string? tenantId, string? clientId, string? tokenFile )
		=> Assert.Throws<ConfigurationException>( () => HybridConnectionProviderFactory.CreateCredential(
			new EntraId { Method = method, TenantId = tenantId, ClientId = clientId, TokenFile = tokenFile } ) );

	// -------------------------------------------------------------------------
	// Tokens
	// -------------------------------------------------------------------------

	[Fact]
	public async Task GetAccessToken_AsksForTheRelayScope()
	{
		var credential = new FakeCredential();
		await new EntraTokenProvider( credential ).GetAccessTokenAsync( CancellationToken.None );

		Assert.Equal( [ "https://relay.azure.net/.default" ], credential.LastScopes! );
	}

	[Fact]
	public async Task GetAccessToken_ReusesATokenUntilNearExpiry()
	{
		var time = new SettableTimeProvider( DateTimeOffset.UtcNow );
		var credential = new FakeCredential { Lifetime = TimeSpan.FromHours( 1 ) };
		var provider = new EntraTokenProvider( credential, time );

		await provider.GetAccessTokenAsync( CancellationToken.None );
		time.Now += TimeSpan.FromMinutes( 50 );
		await provider.GetAccessTokenAsync( CancellationToken.None );
		Assert.Equal( 1, credential.Calls );

		time.Now += TimeSpan.FromMinutes( 6 ); // within RenewBefore of expiry
		await provider.GetAccessTokenAsync( CancellationToken.None );
		Assert.Equal( 2, credential.Calls );
	}

	[Fact]
	public async Task RelayTokenProvider_GivesTheRelayTheCredentialsToken()
	{
		var credential = new FakeCredential();
		var relayProvider = new EntraTokenProvider( credential ).ToRelayTokenProvider();

		var token = await relayProvider.GetTokenAsync( "sb://ns.servicebus.windows.net/lonsql07dev-sql", TimeSpan.FromHours( 1 ) );

		Assert.Equal( credential.LastToken, token.TokenString );
	}

	// -------------------------------------------------------------------------
	// Binding
	// -------------------------------------------------------------------------

	[Fact]
	public void BindAppSettings_EntraId_BindsMethodIgnoringCase()
	{
		var settings = AppConfiguration.BindAppSettings( Configuration( new()
		{
			[ "AppSettings:EntraId:Method" ] = "workloadidentity",
			[ "AppSettings:EntraId:ClientId" ] = "00000000-0000-0000-0000-000000000001",
		} ) );

		Assert.Equal( EntraIdMethod.WorkloadIdentity, settings.EntraId?.Method );
		Assert.Equal( "00000000-0000-0000-0000-000000000001", settings.EntraId?.ClientId );
	}

	[Fact]
	public void BindAppSettings_NoEntraId_IsNull()
		=> Assert.Null( AppConfiguration.BindAppSettings( Configuration( new() { [ "AppSettings:Proxies:0:ListenPort" ] = "1" } ) ).EntraId );

	[Fact]
	public void BindAppSettings_UnknownMethod_IsConfigurationError()
		=> Assert.Throws<ConfigurationException>( () =>
			AppConfiguration.BindAppSettings( Configuration( new() { [ "AppSettings:EntraId:Method" ] = "Password" } ) ) );

	[Fact]
	public void BindAppSettings_MisspeltEntraIdKey_IsConfigurationError()
	{
		var ex = Assert.Throws<ConfigurationException>( () =>
			AppConfiguration.BindAppSettings( Configuration( new()
			{
				[ "AppSettings:EntraId:Method" ] = "AzureCli",
				[ "AppSettings:EntraId:Tenant" ] = "00000000-0000-0000-0000-000000000002",
			} ) ) );

		Assert.Contains( "Tenant", ex.Message );
	}

	// -------------------------------------------------------------------------
	// Helpers
	// -------------------------------------------------------------------------

	private static IConfiguration Configuration( Dictionary<string, string?> values )
		=> new ConfigurationBuilder().AddInMemoryCollection( values ).Build();

	private static int FreePort()
	{
		var listener = new TcpListener( IPAddress.Loopback, 0 );
		listener.Start();
		var port = ( (IPEndPoint) listener.LocalEndpoint ).Port;
		listener.Stop();
		return port;
	}

	private sealed class FakeCredential : TokenCredential
	{
		public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours( 1 );
		public int Calls { get; private set; }
		public string[]? LastScopes { get; private set; }
		public string? LastToken { get; private set; }

		public override AccessToken GetToken( TokenRequestContext requestContext, CancellationToken cancellationToken )
		{
			Calls++;
			LastScopes = requestContext.Scopes;
			var expires = DateTimeOffset.UtcNow + Lifetime;
			LastToken = UnsignedJwt( expires );
			return new AccessToken( LastToken, expires );
		}

		public override ValueTask<AccessToken> GetTokenAsync( TokenRequestContext requestContext, CancellationToken cancellationToken )
			=> ValueTask.FromResult( GetToken( requestContext, cancellationToken ) );

		// The relay SDK reads the token's expiry and audience, so it must look like a JWT.
		private static string UnsignedJwt( DateTimeOffset expires )
		{
			static string Encode( object value )
				=> Convert.ToBase64String( Encoding.UTF8.GetBytes( JsonSerializer.Serialize( value ) ) )
					.TrimEnd( '=' ).Replace( '+', '-' ).Replace( '/', '_' );

			var header = Encode( new { alg = "none", typ = "JWT" } );
			var payload = Encode( new { aud = "https://relay.azure.net/", exp = expires.ToUnixTimeSeconds(), nbf = DateTimeOffset.UtcNow.AddMinutes( -1 ).ToUnixTimeSeconds() } );
			return $"{header}.{payload}.";
		}
	}

	private sealed class SettableTimeProvider( DateTimeOffset now ) : TimeProvider
	{
		public DateTimeOffset Now { get; set; } = now;

		public override DateTimeOffset GetUtcNow()
			=> Now;
	}
}
