using System.Runtime.InteropServices;

using HybridConnectionClientProxy;

using Serilog;

if( args.Contains( "--version" ) )
{
	Console.WriteLine( AppInfo.Version );
	return ExitCodes.Success;
}

if( args.Any( arg => arg is "--help" or "-h" or "-?" or "/?" ) )
{
	Console.WriteLine( Usage() );
	return ExitCodes.Success;
}

// Bootstrap logger — replaced after full configuration is loaded
Log.Logger = Logging.CreateBootstrapLogger();

// Safety nets for faults that escape the handling in ProxyHost, ClientProxy and ClientProxyConnection
AppDomain.CurrentDomain.UnhandledException += ( _, e ) =>
{
	// The runtime terminates the process after this handler returns, so flush now
	Log.Fatal( "Unhandled exception: {Error}", SecretRedactor.RedactText( e.ExceptionObject.ToString() ?? "unknown" ) );
	Log.CloseAndFlush();
};
TaskScheduler.UnobservedTaskException += ( _, e ) =>
	Log.Warning( "Unobserved task exception: {Error}", SecretRedactor.Describe( e.Exception ) );

using var stopping = new CancellationTokenSource();

// SIGTERM (systemd stop, Windows shutdown) and SIGINT (Ctrl+C) request a graceful stop.
// A second signal while already stopping falls through to the default action and terminates the process.
using var sigterm = PosixSignalRegistration.Create( PosixSignal.SIGTERM, RequestStop );
using var sigint  = PosixSignalRegistration.Create( PosixSignal.SIGINT,  RequestStop );

try
{
	return await RunAsync( args, stopping );
}
catch( ConfigurationException ex )
{
	Log.Fatal( "Configuration error: {Error}", ex.Message );
	return ExitCodes.ConfigurationError;
}
catch( ListenException ex )
{
	Log.Fatal( "{Error}", ex.Message );
	return ExitCodes.ListenError;
}
catch( AcceptLoopFailedException )
{
	Log.Fatal( "A proxy stopped accepting connections; exiting so the service manager can restart" );
	return ExitCodes.UnexpectedError;
}
catch( Exception ex )
{
	Log.Fatal( "Unexpected error: {Error}", SecretRedactor.RedactText( ex.ToString() ) );
	return ExitCodes.UnexpectedError;
}
finally
{
	await Log.CloseAndFlushAsync();
}

void RequestStop( PosixSignalContext context )
{
	if( stopping.IsCancellationRequested )
		return;

	context.Cancel = true;
	Log.Information( "Received {Signal}, stopping", context.Signal );
	stopping.Cancel();
}

static async Task<int> RunAsync( string[] args, CancellationTokenSource stopping )
{
	Log.Information( "Reading Configuration" );
	var overlay = ConfigurationLocator.Resolve( args );
	var configuration = AppConfiguration.Build( args, overlay );

	Log.Logger = Logging.CreateLogger( configuration );

	Log.Information(
		"{Application} {Version} starting (pid {ProcessId}, {Runtime}, {OS})",
		AppInfo.Name, AppInfo.Version, Environment.ProcessId, RuntimeInformation.FrameworkDescription, RuntimeInformation.RuntimeIdentifier
	);
	Log.Debug( "Working directory {WorkingDirectory}, binary directory {BaseDirectory}", Environment.CurrentDirectory, AppContext.BaseDirectory );

	var defaultsFile = Path.Combine( AppContext.BaseDirectory, ConfigurationLocator.DefaultsFile );
	Log.Debug( "{AppSettingsDefaultsFile}: {AppSettingsDefaultsFileExists}", defaultsFile, File.Exists( defaultsFile ) );
	Log.Debug(
		"{AppSettingsOverlayFile} (from {AppSettingsOverlaySource}): {AppSettingsOverlayFileExists}",
		overlay.File, AppConfiguration.DescribeSource( overlay.Source ), File.Exists( overlay.File )
	);

	foreach( var entry in AppConfiguration.LoggableSettings( configuration ) )
	{
		Log.Debug( "{Config} = {Value}", entry.Key, entry.Value );
	}

	var appSettings = AppConfiguration.BindAppSettings( configuration );

	Log.Information( "Starting" );
	var host = ProxyHost.Start( appSettings, new HybridConnectionProviderFactory( appSettings.EntraId ).Create );

	Log.Information( "Running" );
	if( !Console.IsInputRedirected && Environment.UserInteractive )
	{
		Console.WriteLine( "Press a key or Ctrl+C to stop" );
		_ = Task.Run( () =>
		{
			try
			{
				Console.ReadKey( true );
				stopping.Cancel();
			}
			catch( InvalidOperationException )
			{
				// no usable console; signals still work
			}
		} );
	}

	var stopRequested = Task.Delay( Timeout.Infinite, stopping.Token );
	var failed = await Task.WhenAny( stopRequested, host.AcceptLoopFailed ) == host.AcceptLoopFailed;

	Log.Information( "Stopping" );
	if( !await host.StopAsync( ProxyHost.ShutdownTimeout ) )
		Log.Warning( "Connections still open after {ShutdownTimeout}; exiting anyway", ProxyHost.ShutdownTimeout );

	if( failed )
		throw new AcceptLoopFailedException();

	Log.Information( "Stopped" );
	return ExitCodes.Success;
}

static string Usage()
	=> $"""
		{AppInfo.Name} {AppInfo.Version}
		Forwards local TCP ports through Azure Relay Hybrid Connections.

		Usage: {AppInfo.Name} [--config <dir>] [--<Section>:<Key>=<Value> ...]

		  --config <dir>  Directory holding appsettings.<MachineName>.json. Defaults to
		                  ${ConfigurationLocator.ConfigDirectoryVariable}, then the config directory beside the binary.
		  --version       Print the version and exit.
		  --help          Print this help and exit.

		Settings are read from appsettings.json, the machine settings file, environment
		variables and the command line, later sources winning. For example:
		  AppSettings__Proxies__0__Name=example
		  AppSettings__Proxies__0__HybridConnectionString=Endpoint=sb://...;EntityPath=...
		  AppSettings__Proxies__0__ListenPort=14331

		Exit codes: {ExitCodes.Success} stopped, {ExitCodes.UnexpectedError} unexpected error, {ExitCodes.ConfigurationError} configuration error, {ExitCodes.ListenError} cannot listen.
		""";
