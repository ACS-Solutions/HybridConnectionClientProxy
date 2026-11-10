using HybridConnectionClientProxy.Settings;

using Microsoft.Extensions.Configuration;

namespace HybridConnectionClientProxy;

internal static class AppConfiguration
{
	/// <summary>
	/// Configuration sections that may be logged (after redaction). Everything else, notably the
	/// process environment, is never logged.
	/// </summary>
	private static readonly string[] LoggableSections = [ AppSettings.Section, "Serilog" ];

	/// <summary>
	/// Builds the configuration. Later sources override earlier ones:
	/// defaults (appsettings.json beside the binary, then platform defaults) → machine settings file →
	/// user secrets (Debug builds) → environment variables → command line.
	/// </summary>
	public static ConfigurationManager Build( string[] args, OverlayLocation overlay )
	{
		if( overlay.IsExplicit && !Directory.Exists( overlay.Directory ) )
			throw new ConfigurationException(
				$"Configuration directory '{overlay.Directory}' (from {DescribeSource( overlay.Source )}) does not exist."
			);

		var configuration = new ConfigurationManager();

		// Relative paths resolve against AppContext.BaseDirectory, not the working directory
		configuration.AddJsonFile( ConfigurationLocator.DefaultsFile, optional: true );
		if( OperatingSystem.IsWindows() )
			configuration.AddJsonFile( ConfigurationLocator.WindowsDefaultsFile, optional: true );
		if( Logging.IsJournald )
			configuration.AddInMemoryCollection( Logging.JournaldDefaults );

		configuration.AddJsonFile( overlay.File, optional: true );
#if DEBUG
		configuration.AddUserSecrets( typeof( AppConfiguration ).Assembly, optional: true );
#endif
		configuration.AddEnvironmentVariables();

		try
		{
			configuration.AddCommandLine( args );
		}
		catch( FormatException ex )
		{
			throw new ConfigurationException( $"Invalid command line: {SecretRedactor.RedactText( ex.Message )}" );
		}

		return configuration;
	}

	public static AppSettings BindAppSettings( IConfiguration configuration )
	{
		var section = configuration.GetSection( AppSettings.Section );
		var appSettings = new AppSettings();
		try
		{
			section.Bind( appSettings );
		}
		catch( InvalidOperationException ex )
		{
			throw new ConfigurationException( SecretRedactor.RedactText( ex.Message ) );
		}

		// The binder silently skips array elements that fail to bind, which would quietly drop a proxy
		// with a mistyped port. Bind each proxy on its own so such mistakes (and misspelt keys) are reported.
		appSettings.Proxies = section.GetSection( nameof( AppSettings.Proxies ) )
			.GetChildren()
			.Select( child =>
			{
				try
				{
					return child.Get<Proxy>( options => options.ErrorOnUnknownConfiguration = true ) ?? new Proxy();
				}
				catch( InvalidOperationException ex )
				{
					throw new ConfigurationException( $"Proxy #{child.Key}: {SecretRedactor.RedactText( ex.Message )}" );
				}
			} )
			.ToArray();

		// Likewise report misspelt EntraId keys rather than ignoring them
		var entraId = section.GetSection( EntraId.Section );
		if( entraId.Exists() )
		{
			try
			{
				appSettings.EntraId = entraId.Get<EntraId>( options => options.ErrorOnUnknownConfiguration = true );
			}
			catch( InvalidOperationException ex )
			{
				throw new ConfigurationException( $"{EntraId.Section}: {SecretRedactor.RedactText( ex.Message )}" );
			}
		}

		return appSettings;
	}

	/// <summary>
	/// The application's own settings with secrets redacted, in key order.
	/// </summary>
	public static IEnumerable<KeyValuePair<string, string?>> LoggableSettings( IConfiguration configuration )
		=> LoggableSections
			.SelectMany( section => configuration.GetSection( section ).AsEnumerable() )
			.Where( pair => pair.Value is not null )
			.OrderBy( pair => pair.Key, StringComparer.OrdinalIgnoreCase )
			.Select( pair => KeyValuePair.Create( pair.Key, SecretRedactor.RedactSetting( pair.Key, pair.Value ) ) );

	public static string DescribeSource( OverlaySource source )
		=> source switch
		{
			OverlaySource.CommandLine         => ConfigurationLocator.ConfigDirectorySwitch,
			OverlaySource.EnvironmentVariable => ConfigurationLocator.ConfigDirectoryVariable,
			OverlaySource.BinaryLocation      => "binary location",
			OverlaySource.WorkingDirectory    => "working directory",
			_                                 => source.ToString(),
		};
}
