using System.Net;
using System.Diagnostics;
using System.Net.Sockets;

using HybridConnectionClientProxy.Settings;

using Xunit;

namespace HybridConnectionClientProxy.Tests;

public class ProxyHostTests
{
	private const int TestTimeoutMs = 10_000;

	private const string Key = "c2VjcmV0LWtleS10aGF0LW11c3Qtbm90LWJlLWxvZ2dlZA==";

	private static AppSettings Settings( params Proxy[] proxies )
		=> new() { Proxies = proxies };

	private static int FreePort()
	{
		var listener = new TcpListener( IPAddress.Loopback, 0 );
		listener.Start();
		var port = ( (IPEndPoint) listener.LocalEndpoint ).Port;
		listener.Stop();
		return port;
	}

	// -------------------------------------------------------------------------
	// Configuration validation
	// -------------------------------------------------------------------------

	[Fact]
	public void Start_NoProxies_IsConfigurationError()
	{
		Assert.Throws<ConfigurationException>( () => ProxyHost.Start( Settings(), ( _, _ ) => throw new UnreachableException() ) );
	}

	[Fact]
	public void Start_MissingListenPort_IsConfigurationError()
	{
		var ex = Assert.Throws<ConfigurationException>( () =>
			ProxyHost.Start( Settings( new Proxy { Name = "db" } ), ( _, _ ) => new FailingProvider() ) );

		Assert.Contains( "db", ex.Message );
		Assert.Contains( "ListenPort", ex.Message );
	}

	[Fact]
	public void Start_InvalidListenAddress_IsConfigurationError()
	{
		Assert.Throws<ConfigurationException>( () =>
			ProxyHost.Start( Settings( new Proxy { ListenPort = 1, ListenIPAddress = "localhost" } ), ( _, _ ) => new FailingProvider() ) );
	}

	[Fact]
	public void Start_MissingConnectionString_IsConfigurationError()
	{
		var ex = Assert.Throws<ConfigurationException>( () =>
			ProxyHost.Start( Settings( new Proxy { Name = "db", ListenPort = FreePort() } ), HybridConnectionProviderFactory.SharedAccessKeyOnly.Create ) );

		Assert.Contains( "HybridConnectionString", ex.Message );
	}

	[Theory]
	[InlineData( "SharedAccessKeyName=cody;SharedAccessKey=" + Key + ";EntityPath=e", "Endpoint" )]
	[InlineData( "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=cody;SharedAccessKey=" + Key, "EntityPath" )]
	[InlineData( "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=cody;EntityPath=e", "SharedAccessKey" )]
	[InlineData( "Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKey=" + Key + ";EntityPath=e", "SharedAccessKeyName" )]
	[InlineData( "this is not a connection string " + Key, null )]
	[InlineData( "Endpoint=not a uri;SharedAccessKeyName=cody;SharedAccessKey=" + Key + ";EntityPath=e", null )]
	public void Start_InvalidConnectionString_IsConfigurationErrorWithoutTheKey( string connectionString, string? expectedInMessage )
	{
		var ex = Assert.Throws<ConfigurationException>( () =>
			ProxyHost.Start(
				Settings( new Proxy { Name = "db", HybridConnectionString = connectionString, ListenPort = FreePort() } ),
				HybridConnectionProviderFactory.SharedAccessKeyOnly.Create
			) );

		Assert.DoesNotContain( Key, ex.Message );
		if( expectedInMessage is not null )
			Assert.Contains( expectedInMessage, ex.Message );
	}

	[Fact]
	public async Task Start_ValidConnectionString_ListensWithoutContactingRelay()
	{
		var port = FreePort();
		var host = ProxyHost.Start(
			Settings( new Proxy
			{
				Name = "db",
				HybridConnectionString = "Endpoint=sb://example.invalid/;SharedAccessKeyName=cody;SharedAccessKey=" + Key + ";EntityPath=e",
				ListenPort = port,
			} ),
			HybridConnectionProviderFactory.SharedAccessKeyOnly.Create
		);

		Assert.Equal( port, Assert.Single( host.ListenEndpoints ).Port );
		Assert.True( await host.StopAsync( TimeSpan.FromSeconds( 5 ) ) );
	}

	[Fact]
	public async Task Start_PortInUse_IsListenErrorAndReleasesOtherPorts()
	{
		var busy = new TcpListener( IPAddress.Loopback, 0 );
		busy.Start();
		var busyPort = ( (IPEndPoint) busy.LocalEndpoint ).Port;
		var freePort = FreePort();

		try
		{
			Assert.Throws<ListenException>( () => ProxyHost.Start(
				Settings( new Proxy { ListenPort = freePort }, new Proxy { ListenPort = busyPort } ),
				( _, _ ) => new FailingProvider()
			) );

			// The first port, bound before the failure, must have been released
			var host = ProxyHost.Start( Settings( new Proxy { ListenPort = freePort } ), ( _, _ ) => new FailingProvider() );
			await host.StopAsync( TimeSpan.FromSeconds( 5 ) );
		}
		finally
		{
			busy.Stop();
		}
	}

	// -------------------------------------------------------------------------
	// Runtime behaviour
	// -------------------------------------------------------------------------

	[Fact]
	public async Task RelayFailure_DropsClientAndKeepsListening()
	{
		using var cts = new CancellationTokenSource( TestTimeoutMs );

		var host = ProxyHost.Start( Settings( new Proxy { Name = "db", ListenPort = FreePort() } ), ( _, _ ) => new FailingProvider() );
		var endpoint = host.ListenEndpoints[0];

		for( int i = 0; i < 3; i++ )
		{
			using var client = new TcpClient();
			await client.ConnectAsync( IPAddress.Loopback, endpoint.Port, cts.Token );

			// The proxy closes the client connection: read returns EOF (or the socket is reset)
			try
			{
				var read = await client.GetStream().ReadAsync( new byte[1], cts.Token );
				Assert.Equal( 0, read );
			}
			catch( IOException )
			{
			}
		}

		Assert.True( await host.StopAsync( TimeSpan.FromSeconds( 5 ) ) );
	}

	[Fact]
	public async Task StopAsync_WithOpenConnection_ClosesItPromptly()
	{
		using var cts = new CancellationTokenSource( TestTimeoutMs );

		var backend = new TcpListener( IPAddress.Loopback, 0 );
		backend.Start();
		var backendPort = ( (IPEndPoint) backend.LocalEndpoint ).Port;

		var host = ProxyHost.Start(
			Settings( new Proxy { ListenPort = FreePort() } ),
			( _, _ ) => new TcpHybridConnectionProvider( backendPort )
		);

		using var client = new TcpClient();
		await client.ConnectAsync( IPAddress.Loopback, host.ListenEndpoints[0].Port, cts.Token );
		using var backendConnection = await backend.AcceptTcpClientAsync( cts.Token );

		var stopwatch = System.Diagnostics.Stopwatch.StartNew();
		Assert.True( await host.StopAsync( TimeSpan.FromSeconds( 5 ) ) );
		Assert.True( stopwatch.Elapsed < TimeSpan.FromSeconds( 5 ) );

		// The backend sees the relayed connection close
		var read = await backendConnection.GetStream().ReadAsync( new byte[1], cts.Token );
		Assert.Equal( 0, read );

		// And the port is no longer accepting
		using var late = new TcpClient();
		await Assert.ThrowsAnyAsync<SocketException>( () => late.ConnectAsync( IPAddress.Loopback, host.ListenEndpoints[0].Port, cts.Token ).AsTask() );

		backend.Stop();
	}

	[Fact]
	public async Task StopAsync_WhileRelayConnecting_DoesNotWaitForRelay()
	{
		using var cts = new CancellationTokenSource( TestTimeoutMs );

		var host = ProxyHost.Start( Settings( new Proxy { ListenPort = FreePort() } ), ( _, _ ) => new HangingProvider() );

		using var client = new TcpClient();
		await client.ConnectAsync( IPAddress.Loopback, host.ListenEndpoints[0].Port, cts.Token );
		await Task.Delay( 200, cts.Token );

		Assert.True( await host.StopAsync( TimeSpan.FromSeconds( 5 ) ) );
	}

	[Fact]
	public async Task RelayRaisedCancellation_IsLoggedAsRelayFailure()
	{
		// A cancellation the relay library raises itself (e.g. a timeout) is a relay failure, not a shutdown
		using var cts = new CancellationTokenSource( TestTimeoutMs );
		var label = $"cancel-{Guid.NewGuid():N}";

		using var capture = LogCapture.Start();
		var host = ProxyHost.Start(
			Settings( new Proxy { Name = label, ListenPort = FreePort() } ),
			( _, _ ) => new SelfCancellingProvider()
		);

		using( var client = new TcpClient() )
		{
			await client.ConnectAsync( IPAddress.Loopback, host.ListenEndpoints[0].Port, cts.Token );
			try { Assert.Equal( 0, await client.GetStream().ReadAsync( new byte[1], cts.Token ) ); }
			catch( IOException ) { } // reset instead of EOF is also a drop
		}

		Assert.True( await host.StopAsync( TimeSpan.FromSeconds( 5 ) ) );
		Assert.Contains(
			capture.Events,
			e => e.Level == Serilog.Events.LogEventLevel.Warning
				&& e.RenderMessage().Contains( label )
				&& e.RenderMessage().Contains( "relay connection failed" )
				&& e.RenderMessage().Contains( "TaskCanceledException" )
		);
	}

	/// <summary>Simulates the relay library timing out with its own cancellation.</summary>
	private sealed class SelfCancellingProvider : IHybridConnectionProvider
	{
		public Task<Stream> CreateConnectionAsync( CancellationToken cancellationToken = default )
			=> Task.FromException<Stream>( new TaskCanceledException( "relay operation timed out" ) );
	}

	/// <summary>Simulates an unreachable relay or a rejected key.</summary>
	private sealed class FailingProvider : IHybridConnectionProvider
	{
		public Task<Stream> CreateConnectionAsync( CancellationToken cancellationToken = default )
			=> Task.FromException<Stream>( new IOException( "relay unavailable; SharedAccessKey=" + Key ) );
	}

	/// <summary>Simulates a relay that never answers but honours cancellation.</summary>
	private sealed class HangingProvider : IHybridConnectionProvider
	{
		public async Task<Stream> CreateConnectionAsync( CancellationToken cancellationToken = default )
		{
			await Task.Delay( Timeout.Infinite, cancellationToken );
			throw new UnreachableException();
		}
	}
}
