using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace HybridConnectionClientProxy.Tests;

/// <summary>
/// Integration tests that run against a real Azure Hybrid Connection endpoint.
///
/// They use the first proxy in <c>AppSettings</c>, with <c>AppSettings:EntraId</c> for a connection string
/// without a shared access key, read from <c>appsettings.Test.json</c> beside the test assembly and then from
/// environment variables prefixed <c>HCCP_TEST_</c> (<c>HCCP_TEST_AppSettings__Proxies__0__HybridConnectionString</c>,
/// <c>HCCP_TEST_AppSettings__EntraId__Method</c>). With neither (pull requests, local runs without credentials)
/// every test is skipped.
///
/// The endpoint referenced by the first proxy must have a listener registered and running, in front of an SMTP
/// server, for data-flow assertions to pass.
/// </summary>
public class AzureIntegrationTests
{
	private const int TimeoutMs = 30_000;

	// -------------------------------------------------------------------------
	// Tests
	// -------------------------------------------------------------------------

	[Fact]
	public async Task AzureProxy_WhenClientConnects_ConnectionEstablishedWithoutError()
	{
		var provider = CreateProviderOrSkip();

		using var cts = new CancellationTokenSource( TimeoutMs );

		var (proxyListener, proxyPort) = StartListener();
		var proxyTask = ClientProxy.Create( provider, proxyListener, cts.Token );

		using var client = new TcpClient();
		await client.ConnectAsync( IPAddress.Loopback, proxyPort, cts.Token );

		// Give the relay a moment to confirm the connection is live
		await Task.Delay( 1_000, cts.Token );

		await cts.CancelAsync();
		proxyListener.Stop();
		await proxyTask;
	}

	[Fact]
	public async Task AzureProxy_WhenClientDisconnects_CleansUpPromptly()
	{
		var provider = CreateProviderOrSkip();

		using var cts = new CancellationTokenSource( TimeoutMs );

		var (proxyListener, proxyPort) = StartListener();
		var proxyTask = ClientProxy.Create( provider, proxyListener, cts.Token );

		var client = new TcpClient();
		await client.ConnectAsync( IPAddress.Loopback, proxyPort, cts.Token );
		await Task.Delay( 500, cts.Token ); // let the relay connection settle

		// Disconnect the client — this is the scenario that used to hang
		client.Close();

		// The proxy should clean up within a few seconds; if it hangs the test
		// times out and fails, confirming the regression is present.
		await Task.Delay( 3_000, cts.Token );

		await cts.CancelAsync();
		proxyListener.Stop();
		await proxyTask;
	}

	[Fact]
	public async Task AzureProxy_SmtpEhlo_ServerResponds250()
	{
		var provider = CreateProviderOrSkip();

		using var cts = new CancellationTokenSource( TimeoutMs );

		var (proxyListener, proxyPort) = StartListener();
		_ = ClientProxy.Create( provider, proxyListener, cts.Token );

		using var client = new TcpClient();
		await client.ConnectAsync( IPAddress.Loopback, proxyPort, cts.Token );
		using var reader = new StreamReader( client.GetStream(), Encoding.ASCII, leaveOpen: true );
		using var writer = new StreamWriter( client.GetStream(), Encoding.ASCII, leaveOpen: true ) { NewLine = "\r\n", AutoFlush = true };

		// Server must send a 220 greeting
		var greeting = await ReadSmtpResponseAsync( reader, cts.Token );
		Assert.StartsWith( "220", greeting );

		// Send EHLO and expect 250
		await writer.WriteLineAsync( "EHLO test" );
		var response = await ReadSmtpResponseAsync( reader, cts.Token );
		Assert.StartsWith( "250", response );

		// Quit cleanly
		await writer.WriteLineAsync( "QUIT" );
		await ReadSmtpResponseAsync( reader, cts.Token );

		await cts.CancelAsync();
		proxyListener.Stop();
	}

	// -------------------------------------------------------------------------
	// Helpers
	// -------------------------------------------------------------------------

	private const string SkipReason = "No relay test settings (appsettings.Test.json or HCCP_TEST_ environment variables)";

	private static IHybridConnectionProvider CreateProviderOrSkip()
	{
		var configuration = new ConfigurationBuilder()
			.AddJsonFile( Path.Combine( AppContext.BaseDirectory, "appsettings.Test.json" ), optional: true )
			.AddEnvironmentVariables( "HCCP_TEST_" )
			.Build();

		var settings = AppConfiguration.BindAppSettings( configuration );
		var proxy = settings.Proxies.FirstOrDefault();
		if( string.IsNullOrWhiteSpace( proxy?.HybridConnectionString ) )
			Assert.Skip( SkipReason );

		return new HybridConnectionProviderFactory( settings.EntraId ).Create( proxy, "azure-test" );
	}

	private static (TcpListener listener, int port) StartListener()
	{
		var listener = new TcpListener( IPAddress.Loopback, 0 );
		listener.Start();
		return (listener, ((IPEndPoint) listener.LocalEndpoint).Port);
	}

	/// <summary>
	/// Reads a (possibly multi-line) SMTP response and returns the final line.
	/// Multi-line responses use "NNN-text" for continuation lines and "NNN text" for the last.
	/// </summary>
	private static async Task<string> ReadSmtpResponseAsync( StreamReader reader, CancellationToken ct )
	{
		string? line;
		do
		{
			line = await reader.ReadLineAsync( ct );
			if( line is null ) throw new EndOfStreamException( "SMTP server closed the connection." );
		}
		while( line.Length >= 4 && line[3] == '-' ); // continuation line

		return line;
	}
}
