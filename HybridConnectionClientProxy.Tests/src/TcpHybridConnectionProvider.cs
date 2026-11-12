using System.Net;
using System.Net.Sockets;

using HybridConnectionClientProxy;

namespace HybridConnectionClientProxy.Tests;

/// <summary>
/// Test double for <see cref="IHybridConnectionProvider"/> that connects to a local TCP server
/// instead of Azure Service Bus. This lets integration tests verify the full proxy pipeline
/// without requiring live Azure credentials.
/// </summary>
internal sealed class TcpHybridConnectionProvider : IHybridConnectionProvider
{
	private readonly int _targetPort;

	public TcpHybridConnectionProvider( int targetPort )
	{
		_targetPort = targetPort;
	}

	public async Task<Stream> CreateConnectionAsync( CancellationToken cancellationToken = default )
	{
		var client = new TcpClient();
		try
		{
			await client.ConnectAsync( IPAddress.Loopback, _targetPort, cancellationToken );
			return client.GetStream();
		}
		catch
		{
			// Cancellation can win the race with a connect the server has already accepted: close the socket, as
			// HybridConnectionClientProvider does with a late relay connection, or the server waits for it forever.
			client.Dispose();
			throw;
		}
	}
}
