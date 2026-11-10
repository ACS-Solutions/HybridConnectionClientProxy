# Hybrid Connection Client Proxy
*Provides the client side of a TCP relay via Azure Service Bus*

[Azure Hybrid Connections](https://learn.microsoft.com/en-us/azure/app-service/app-service-hybrid-connections) is a neat way to access TCP services on-prem or otherwise not exposed directly on the Internet. 

If you run your code on-prem, you can usually connect directly to your on-prem resources (e.g. database or web servers). If you move to Azure Web App, Hybrid Connections allow you to still connect to your on-prem resources. But what if you're a developer working from home, or you want to run that sweet firewall busting magic from elsewhere, like a container in AWS? Well, the official line is you're fresh out of luck.

## How does it work in Azure then?

Under the hood, Microsoft implement the client-side (running in your Azure Web App) of the Hybrid Connections by loading a process called workerforwarder.exe under the active website in IIS. You can see it in the Process Explorer in [Kudu](https://\<yoursite\>.scm.azurewebsites.net/ProcessExplorer) and if you look around the DebugConsole you'll see the binaries in `C:\Program Files\PortBridge`. It's a bit old and its configuration comes from an environment variable called `WEBSITE_RELAYS` whose contents are Base64-encoded XML. It's written in old-schood .NET Framework 4.7. The clumsy configuration alone means it's not really usable as-is in your dev or production environment.

## Enter the Hybrid Connection Client Proxy
*Connect to on-prem resources from anywhere.*

Hybrid Connection Client Proxy is 
- Written as a console app in .NET 10
- Uses the official Microsoft.Azure.Relay to provide the Service Bus connections and implement the Hybrid Connection protocol
- Configured from JSON files, environment variables or the command line (and UserSecrets for development)
- Runs on Windows and Linux, interactively or as a service (systemd, with logs in the journal)

It listens on one or more local TCP ports and tunnels each accepted connection through Azure Relay to the
on-prem endpoint registered behind the hybrid connection.

## Installing a release

Each [release](https://github.com/ACS-Solutions/HybridConnectionClientProxy/releases) has:

| Asset | Contents | Needs |
|---|---|---|
| `HybridConnectionClientProxy-<version>-linux-x64.tar.gz` | `HybridConnectionClientProxy` (single file, framework-dependent), `appsettings.json`, `README.md`, `LICENSE.txt` | .NET 10 runtime (`Microsoft.NETCore.App` 10.0.x; on Ubuntu `apt install dotnet-runtime-10.0`) |
| `HybridConnectionClientProxy-<version>-win-x64.zip` | `HybridConnectionClientProxy.exe` (single file, self-contained), `appsettings.json`, `appsettings.Windows.json`, `README.md`, `LICENSE.txt` | nothing |
| `SHA256SUMS` | SHA-256 of both archives, in `sha256sum` format | |

The archives have no top-level directory; extract them into the directory you want to run from.

### Verifying a download

Check the checksum and the build provenance attestation, which proves the file was built by this repository's
release workflow from the tagged commit:

```sh
VERSION=1.0.0
gh release download "v$VERSION" -R ACS-Solutions/HybridConnectionClientProxy
sha256sum --check --ignore-missing SHA256SUMS
gh attestation verify "HybridConnectionClientProxy-$VERSION-linux-x64.tar.gz" -R ACS-Solutions/HybridConnectionClientProxy
```

Then, for example:

```sh
mkdir -p ~/.local/lib/hybridconnectionclientproxy
tar -xzf "HybridConnectionClientProxy-$VERSION-linux-x64.tar.gz" -C ~/.local/lib/hybridconnectionclientproxy
~/.local/lib/hybridconnectionclientproxy/HybridConnectionClientProxy --version
```

## Configuration

Settings are read from these sources, each overriding the ones before it:

1. `appsettings.json` beside the binary (defaults, including logging), then `appsettings.Windows.json` on Windows
2. The machine settings file `appsettings.{MachineName}.json` (optional)
3. UserSecrets (Debug builds only)
4. Environment variables
5. The command line (`--AppSettings:Proxies:0:ListenPort=14331`)

No settings file is required: the proxies can be configured entirely from environment variables.

### Proxies

Each proxy is an entry in `AppSettings:Proxies`:

| Setting | Required | Meaning |
|---|---|---|
| `Name` | no | Label used in log messages |
| `HybridConnectionString` | yes | `Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=<rule>;SharedAccessKey=<key>;EntityPath=<hybrid connection>`, or without `SharedAccessKeyName` and `SharedAccessKey` to [sign in with Entra ID](#entra-id-sign-in-instead-of-a-key) |
| `ListenPort` | yes | Local TCP port to listen on |
| `ListenIPAddress` | no | Local address to listen on; defaults to `127.0.0.1` |

As environment variables (`__` separates the levels, the number is the proxy's index):

```sh
AppSettings__Proxies__0__Name=lonsql07dev-sql
AppSettings__Proxies__0__HybridConnectionString='Endpoint=sb://clientportal.servicebus.windows.net/;SharedAccessKeyName=cody;SharedAccessKey=...;EntityPath=lonsql07dev-sql'
AppSettings__Proxies__0__ListenPort=14331
AppSettings__Proxies__1__Name=loniridium02
AppSettings__Proxies__1__HybridConnectionString='Endpoint=sb://clientportal.servicebus.windows.net/;SharedAccessKeyName=cody;SharedAccessKey=...;EntityPath=loniridium02'
AppSettings__Proxies__1__ListenPort=14332
```

As JSON (in the machine settings file):

```json
{
	"AppSettings": {
		"Proxies": [
			{
				"Name": "servername",
				"HybridConnectionString": "Endpoint=sb://xxx.servicebus.windows.net/;SharedAccessKeyName=defaultSender;SharedAccessKey=whatever;EntityPath=servername",
				"ListenPort": 12345
			}
		]
	}
}
```

Or in UserSecrets, the usual transform:

```json
{
	"AppSettings:Proxies:0:HybridConnectionString": "Endpoint=sb://xxx.servicebus.windows.net/;SharedAccessKeyName=defaultSender;SharedAccessKey=whatever;EntityPath=servername",
	"AppSettings:Proxies:0:ListenPort": 50080
}
```

The settings are logged at startup (at `Debug` level) with keys and connection-string secrets replaced by `***`.

### Where the machine settings file is found

The first of these that applies:

1. `--config <dir>` on the command line
2. The `HCCP_CONFIG_DIR` environment variable
3. The `config` directory beside the binary's directory: if the binary is under a `bin` directory, `config` is a
   sibling of that `bin` (so `project/bin/Debug/net10.0` → `project/config`, `C:\Tools\Proxy\bin` → `C:\Tools\Proxy\config`);
   otherwise it is a sibling of the binary's directory
4. The same, relative to the working directory (how earlier versions found it)

You can keep your config separate from the binaries and point multiple machines at the same folder to get
per-machine configuration. An explicit directory (1 or 2) must exist; the file in it is optional.

### Logging

Logging uses [Serilog](https://github.com/serilog/serilog-settings-configuration) and is configured in the
`Serilog` section, so it can be changed in any of the sources above (for example `Serilog__MinimumLevel=Information`).

- By default logs go to the console only. Colours are used only on an interactive terminal.
- On Windows, `appsettings.Windows.json` also writes a daily rolling file to `%TEMP%\HybridConnectionClientProxy-<date>.log`
  and to the debugger output, as earlier versions did.
- When stdout is the systemd journal (`JOURNAL_STREAM` names the stdout socket), console output has no timestamps
  or colours, and each line is tagged with its syslog priority, so `journalctl --user -u hybridconnectionclientproxy -p warning` works.

## Running

```sh
HybridConnectionClientProxy [--config <dir>] [--<Section>:<Key>=<Value> ...]
HybridConnectionClientProxy --version
HybridConnectionClientProxy --help
```

Interactively, press a key or Ctrl+C to stop. `SIGTERM` (e.g. `systemctl stop`) and `SIGINT` stop the listeners
and close open connections, waiting at most 5 seconds.

If the relay or the on-prem endpoint is unavailable, the client connection that needed it is dropped and a
warning logged; the proxy keeps listening.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Stopped cleanly (signal or key press), or `--version` / `--help` |
| 1 | Unexpected error, including a proxy that stops accepting connections (a local fault; restarting re-binds every port) |
| 2 | Configuration error (missing or invalid settings, including the `Serilog` section). Restarting will not help until the configuration is fixed |
| 3 | A listen port could not be bound (in use or not permitted) |

An unhandled exception is logged (Fatal, redacted) before the runtime aborts the process, so it ends by signal
rather than with one of these codes; systemd's `Restart=on-failure` treats both as failures.

### As a systemd user service

This keeps the connection strings out of files and out of the unit: each is encrypted with
[`systemd-creds`](https://www.freedesktop.org/software/systemd/man/latest/systemd-creds.html) (bound to the
machine's TPM2 where available) and only decrypted into the environment of the service process when it starts.

Store each connection string as a credential (it is read from stdin, so it does not end up in your shell history):

```sh
mkdir -p ~/.config/credstore.encrypted
systemd-ask-password -n "lonsql07dev-sql connection string:" \
	| systemd-creds encrypt --user --name=hccp.lonsql07dev-sql - ~/.config/credstore.encrypted/hccp.lonsql07dev-sql
```

`~/.config/systemd/user/hybridconnectionclientproxy.service`:

```ini
[Unit]
Description=Azure Relay Hybrid Connection client proxy
Documentation=https://github.com/ACS-Solutions/HybridConnectionClientProxy

[Service]
Type=exec
WorkingDirectory=%h/.local/lib/hybridconnectionclientproxy

# Decrypted into $CREDENTIALS_DIRECTORY, visible only to this service.
# Imports every hccp.* credential from ~/.config/credstore.encrypted; alternatively name each one with
# LoadCredentialEncrypted=hccp.lonsql07dev-sql:/path/to/file.cred
ImportCredential=hccp.*

Environment=AppSettings__Proxies__0__Name=lonsql07dev-sql
Environment=AppSettings__Proxies__0__ListenPort=14331
Environment=AppSettings__Proxies__1__Name=loniridium02
Environment=AppSettings__Proxies__1__ListenPort=14332

# Build the connection-string variables from the credentials, then exec so the proxy receives SIGTERM directly.
# $$ stops systemd expanding the variables itself.
ExecStart=/bin/sh -c '\
	AppSettings__Proxies__0__HybridConnectionString="$$(cat "$$CREDENTIALS_DIRECTORY/hccp.lonsql07dev-sql")" \
	AppSettings__Proxies__1__HybridConnectionString="$$(cat "$$CREDENTIALS_DIRECTORY/hccp.loniridium02")" \
	exec ./HybridConnectionClientProxy'

Restart=on-failure
RestartSec=5
# A configuration error will not fix itself
RestartPreventExitStatus=2
TimeoutStopSec=15

[Install]
WantedBy=default.target
```

```sh
systemctl --user daemon-reload
systemctl --user enable --now hybridconnectionclientproxy
journalctl --user -u hybridconnectionclientproxy -f
loginctl enable-linger "$USER"   # keep it running when you are not logged in
```

### One Send-only key per machine

Give each machine its own shared access policy with only the **Send** claim, so it can open connections but not
listen or manage, and a single machine's key can be revoked (or rotated) without touching the others. The policy
can be on the namespace (covering all its hybrid connections) or on each hybrid connection:

```sh
az relay namespace authorization-rule create \
	--resource-group <rg> --namespace-name clientportal --name cody --rights Send

az relay namespace authorization-rule keys list \
	--resource-group <rg> --namespace-name clientportal --name cody \
	--query primaryConnectionString -o tsv
```

The connection string for a proxy is the namespace connection string with `;EntityPath=<hybrid connection>`
appended. To revoke a machine, delete its rule (or regenerate its keys).

### Entra ID sign-in instead of a key

A proxy whose `HybridConnectionString` has no shared access key
(`Endpoint=sb://clientportal.servicebus.windows.net/;EntityPath=lonsql07dev-sql`) signs in with Microsoft Entra ID,
so no key is stored anywhere. The identity needs the **Azure Relay Sender** role on the hybrid connection (or its
namespace). Shared-key proxies keep working, and the two can be mixed. How to sign in is set once, in
`AppSettings:EntraId`, for every key-less proxy:

| `Method` | Signs in as | Other settings |
|---|---|---|
| `ManagedIdentity` | The Azure resource's managed identity | `ClientId` for a user-assigned identity; leave it out for the system-assigned one |
| `WorkloadIdentity` | An app or user-assigned identity, by exchanging a federated token read from a file (Kubernetes workload identity, or an OIDC issuer of your own) | `TenantId`, `ClientId`, `TokenFile`; each defaults to `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_FEDERATED_TOKEN_FILE` |
| `AzureCli` | The account `az login` (or `azure/login` in GitHub Actions) signed in | `TenantId`, optional |

```sh
AppSettings__EntraId__Method=WorkloadIdentity
AppSettings__EntraId__TenantId=<tenant id>
AppSettings__EntraId__ClientId=<identity's client id>
AppSettings__EntraId__TokenFile=/run/user/1000/hccp/token
AppSettings__Proxies__0__HybridConnectionString='Endpoint=sb://clientportal.servicebus.windows.net/;EntityPath=lonsql07dev-sql'
AppSettings__Proxies__0__ListenPort=14331
```

The token is renewed shortly before it expires; a workload identity's token file must be kept fresh by whatever
writes it. `TokenFile` is logged as `***`, like anything with "Token" in its name.

## Building a release

Pushing a tag `v<version>` (e.g. `v1.2.0`, or `v1.2.0-rc.1` for a pre-release) runs
[`.github/workflows/release.yml`](.github/workflows/release.yml): it builds and tests, packages with
[`eng/package.sh`](eng/package.sh), attests build provenance and creates the GitHub release. Running the workflow
manually builds and packages without attesting or releasing. To package locally:

```sh
eng/package.sh 1.2.0 dist
```

## Contributing

Yes please! This is a rough first cut, enough to get it working, but I haven't stress-tested it for memory leaks or other issues so be cautious using it in a production environment.

Fork me and send in your PR :-)
