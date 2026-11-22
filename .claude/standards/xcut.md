# Cross-cutting concerns: house rules

Status: agreed with the owner. Drafted by `/xcut:review survey` at 680adf0; the open questions from that survey are
resolved under "Decisions" at the end, and the code was brought into line in the same change.

These rules override the generic `xcut:standards`. Reviews cite the `H-…` IDs.

Context: a single-process .NET 11 console app / systemd service. No DI container, no ASP.NET Core,
no hosted-service framework. Static Serilog `Log` throughout.

---

## Logging (LOG)

**H-LOG-1 Logger.** Use the static Serilog `Log` class. No `ILogger<T>`, no DI. `Program.cs` installs
`Logging.CreateBootstrapLogger()` first, replaces it with `Logging.CreateLogger(configuration)` once
configuration is built, and calls `Log.CloseAndFlushAsync()` in the top-level `finally`.
Sinks and levels come from the `Serilog` configuration section; sink assemblies must be named in
`ConfigurationReaderOptions` in `Logging.CreateLogger` (single-file publish has no deps manifest).
`LogContext` is not used, so neither logger enables `Enrich.FromLogContext()`.
Exemplars: `src/Logging.cs`, `src/Program.cs`.

**H-LOG-2 Templates and property names.** Always use message templates with PascalCase named properties,
never interpolation. Use these names for these things:

| Thing | Property |
|---|---|
| Proxy label (`#<index>` or `#<index> (<Name>)`) | `{Proxy}` |
| Per-proxy connection sequence number | `{ConnectionId}` |
| Client remote endpoint | `{Remote}` |
| Bound local endpoint | `{ListenEndpoint}` |
| Redacted exception description | `{Error}` |
| Configuration key / redacted value | `{Config}` / `{Value}` |
| POSIX signal | `{Signal}` |
| App name / version | `{Application}` / `{Version}` |
| Shutdown drain timeout | `{ShutdownTimeout}` |

`{Proxy}` stays a single label; it is not split into index and name.
Exemplars: `ClientProxyConnection.Run`, `ClientProxy.Run`.

**H-LOG-3 Context by message prefix, not scopes.** Logs about a proxy or connection start with
`"Proxy {Proxy}: "` and, for a connection, `"Proxy {Proxy}: connection {ConnectionId} "`, followed by the
event (`accepted from`, `relayed`, `closed`, `dropped, <reason>`, `failed`). The proxy label is computed once
in `ProxyHost.Start` and passed down.

**H-LOG-4 Level policy.**
- **Debug**: per-connection lifecycle (accepted, relayed, closed), including the client's `{Remote}` endpoint
  (acceptable: listeners default to loopback, and it stays at Debug); the redacted configuration dump;
  file/directory diagnostics at startup.
- **Information**: process lifecycle (`Reading Configuration`, `<App> <Version> starting …`, `Starting`,
  `Running`, `Received {Signal}, stopping`, `Stopping`, `Stopped`) and each proxy's
  `listening on {ListenEndpoint}`.
- **Warning**: a handled per-connection or per-accept failure where the proxy carries on (relay
  connection failed → client dropped; transient accept failure → continuing), a shutdown that ran out of
  time, and an unobserved task exception.
- **Error**: an unexpected failure of a connection, or a proxy's accept loop stopping.
- **Fatal**: only immediately before the process ends: the `Program.cs` top-level catches (before returning a
  non-zero exit code) and the `AppDomain.UnhandledException` handler.

**H-LOG-5 Exceptions are never passed to the logger.** Do not use `Log.X(ex, …)`. Serilog would render
`ex.ToString()` unredacted. Instead log `{Error}` with `SecretRedactor.Describe(ex)` (one line:
`Type: message ---> Type: message`, redacted). No stack traces below Fatal. The only places that keep the
stack are the last-resort catch in `Program.cs` and the `AppDomain.UnhandledException` handler, which log
`SecretRedactor.RedactText(ex.ToString())`. Exception messages already guaranteed secret-free
(`ConfigurationException`, `ListenException`) may be logged as `ex.Message`. The exception is logged once,
by the handler that decides what happens next.
Exemplars: `ClientProxyConnection.Run`, `src/Program.cs`.

**H-LOG-6 Redaction.** Anything that can contain a credential goes through `SecretRedactor` before it
reaches a log or an exception message:
- configuration values: `SecretRedactor.RedactSetting(key, value)` (connection strings keep only
  `Endpoint`, `EntityPath`, `SharedAccessKeyName`, `OperationTimeout`, `Authentication`);
- free text and third-party exception messages: `SecretRedactor.RedactText`;
- exceptions: `SecretRedactor.Describe`.
Only the `AppSettings` and `Serilog` sections are ever dumped (`AppConfiguration.LoggableSettings`);
the process environment is never logged. New credential-bearing settings or token formats must be added
to `SecretRedactor` with a test in `SecretRedactorTests`.
Exemplars: `src/SecretRedactor.cs`, `AppConfiguration.LoggableSettings`.

**H-LOG-7 Volume.** Nothing is logged inside the byte-copy pumps. Per-connection events are Debug only;
Warning/Error per connection only on failure.

**H-LOG-8 Actionable warnings and errors.** A Warning/Error states the proxy (and connection), what
failed, and what happens next, e.g. `"…dropped, relay connection failed: {Error}"`,
`"…accept failed, continuing: {Error}"`, `"…stopped accepting connections: {Error}"`,
`"Connections still open after {ShutdownTimeout}; exiting anyway"`.

**H-LOG-9 Startup, shutdown and identity.** Startup logs name, version, pid, runtime and RID at Information,
then the redacted configuration at Debug. Shutdown logs the signal, `Stopping` and `Stopped`.
Per-event identity properties (application, version, machine, process) are added only by Serilog enrichers
configured in the `Serilog` section (`Properties`, or `Enrich` with packages such as
`Serilog.Enrichers.Environment`), and only when a structured sink needs them. Today the only sinks are text,
and under systemd the journal already records host, PID and unit, so none are added beyond `Application`
in `Serilog:Properties`. Do not add identity properties in code.

**H-LOG-10 Output formats.** Console is the default sink on every platform. When stdout is the systemd
journal (`Logging.IsJournald`: `JOURNAL_STREAM`'s inode matches the stdout socket; the variable alone is
inherited by child processes and is not enough), the console template is `Logging.JournaldOutputTemplate`:
syslog priority prefix from `SyslogPriorityEnricher`, no timestamp. On Windows, `appsettings.Windows.json`
adds a rolling file under `%TEMP%` and the Debug sink; the Debug sink is never enabled elsewhere (on Linux,
.NET routes Debug output to syslog, duplicating every journal line). `Console.WriteLine` is reserved for
`--version`, `--help` and the interactive "Press a key" prompt; everything else is logged.

This is how HCCP meets generic LOG9. It compares only the inode, read from the `/proc/self/fd/1` link target
(`socket:[<inode>]`), rather than `device:inode` from `fstat`: .NET has no managed `fstat`, and sockets all
share the sockfs device, so the inode identifies the journal socket. Do not flag the missing device comparison.

## Telemetry (TEL)

**H-TEL-1 Logs are the only telemetry, deliberately.** No metrics, tracing or health endpoint, and no
systemd readiness or watchdog notification: units use `Type=exec`. Startup is quick and every failure
before the proxies are listening ends the process with exit code 2 or 3, so readiness notification adds
little. Do not flag the absence of metrics, tracing, health checks or `sd_notify`.

## Error handling (ERR)

**H-ERR-1 Exit-code contract.** The process reports failure only through `ExitCodes`:

| Code | Meaning | Raised by |
|---|---|---|
| 0 | stopped cleanly, `--version`, `--help` | |
| 1 | unexpected error, including a proxy whose accept loop stopped | `AcceptLoopFailedException`, any other exception |
| 2 | configuration error, including an invalid `Serilog` section | `ConfigurationException` |
| 3 | a listen port could not be bound | `ListenException` |

A systemd unit can set `RestartPreventExitStatus=2` so a configuration error is not restarted in a loop.
Each code maps to typed exceptions caught in `Program.cs`. A new failure category needs a typed exception,
an `ExitCodes` constant, a catch in `Program.cs`, and updates to the exit-code text in `Usage()` and the
README. Changing what a code means is a contract change: tell the claude-hosts maintainers (cody's unit).
Exemplars: `src/ExitCodes.cs`, `src/Program.cs`.

**H-ERR-2 Typed startup exceptions.** Invalid configuration throws `ConfigurationException`; a bind failure
throws `ListenException` wrapping the `SocketException`. Messages start with `Proxy <label>: ` when about
a proxy, name the setting key, include the offending value unless it is sensitive, and must never contain
secrets (wrap third-party messages with `SecretRedactor.RedactText`/`Describe`). Errors from building the
logger (`Logging.CreateLogger`) are configuration errors too.
Exemplars: `ProxyHost.Start`, `src/HybridConnectionProviderFactory.cs`, `Logging.CreateLogger`.

**H-ERR-3 Narrow catches, `when` filters.** Catch specific types, using `when` filters to narrow further.
Broad `catch (Exception)` is allowed only at these boundaries, and must log:
the top level (`Program.cs`), a proxy's accept loop (`ClientProxy.Run`), a connection
(`ClientProxyConnection.Run`), and the relay-connect call, where any failure that is not our own
cancellation means "relay unavailable: drop this client".

**H-ERR-4 Empty catches.** Allowed only for best-effort cleanup after the outcome is already decided
(closing streams to unblock reads, awaiting pumps after closing, stopping listeners) and for expected
conditions. Every empty catch carries a comment saying why; there are no exemptions.
Exemplar: `ClientProxyConnection.Run`, `ProxyHost.StopListeners`.

**H-ERR-5 Cancellation.** `OperationCanceledException` (and, at shutdown, `ObjectDisposedException` and
`SocketException`) is swallowed silently only when our own token is actually cancelled
(`when( cancellation.IsCancellationRequested … )` / `when( cts.IsCancellationRequested )`). A cancellation
raised by a library for its own reasons, such as a relay timeout surfacing as `TaskCanceledException`, is a
failure and is handled and logged like any other.
Exemplars: `ClientProxy.Run`, `ClientProxyConnection.Run`.

**H-ERR-6 Background tasks.** Every task started without being awaited either catches all of its own
exceptions (connection tasks), or is tracked and awaited at shutdown (`ClientProxy._connections`,
`ProxyHost._proxies`), or has its fault observed in a continuation (abandoned relay connect in
`HybridConnectionClientProvider`). Discards with `_ =` are only for tasks that cannot fault or whose fault
is observed.

**H-ERR-7 Process-wide safety net.** `Program.cs` registers `AppDomain.CurrentDomain.UnhandledException`
(log Fatal with the redacted stack, then flush; the runtime then aborts the process) and
`TaskScheduler.UnobservedTaskException` (log Warning with `SecretRedactor.Describe`). They catch gaps in
H-ERR-6; they are not a substitute for it.

## Configuration (CFG)

**H-CFG-1 Sources and precedence.** `ConfigurationManager` layers, later winning: `appsettings.json` →
`appsettings.Windows.json` (Windows) → journald defaults (in-memory) → `appsettings.<MachineName>.json`
in the directory from `ConfigurationLocator` → user secrets (Debug only) → environment variables →
command line. Exemplar: `AppConfiguration.Build`.

**H-CFG-2 Typed binding, strict per proxy.** Bind to `Settings.AppSettings` / `Settings.Proxy`. Each proxy is
bound individually with `ErrorOnUnknownConfiguration = true` so misspelt keys and mistyped values fail
instead of silently dropping the proxy. Binder errors become `ConfigurationException` (redacted).
Exemplar: `AppConfiguration.BindAppSettings`.

**H-CFG-3 Validate everything before side effects.** All proxies are validated (port range, required port,
IP address, connection string parseable and complete) before any listener is bound. Then all listeners are
bound before any proxy starts; a failure stops the ones already bound.
Exemplar: `ProxyHost.Start`.

**H-CFG-4 Required vs defaulted.** Required: `HybridConnectionString`, `ListenPort` (0 means missing).
Optional: `Name` (labels only), `ListenIPAddress` (default `127.0.0.1`). An explicit `--config` /
`HCCP_CONFIG_DIR` directory must exist, but the file in it is optional. New settings are documented in the
README's Proxies table and state their default.

**H-CFG-5 No committed secrets.** Committed JSON holds only placeholders
(`HybridConnectionClientProxy/config/appsettings.template.json`). Real connection strings come from environment
variables (systemd-creds in production), user secrets (development) or an untracked machine settings file.
CI writes `appsettings.Test.json` from the `APPSETTINGS` secret at build time. `.gitignore` covers
`**/config/*.json` (except the template) and `**/appsettings.Test.json`; a new place that can hold secrets
gets an ignore rule in the same change.

## Resilience and resources (RES)

**H-RES-1 Relay failures are not retried.** If the relay connection fails, that client connection is dropped
with a Warning and the proxy keeps listening; the client application retries. A transient accept
`SocketException` waits `ClientProxy.AcceptRetryDelay` (100 ms) and continues.

**H-RES-2 Relay connect timeout.** `HybridConnectionClient.CreateConnectionAsync` cannot be cancelled. It is
bounded by the library's operation timeout (configurable via `OperationTimeout` in the connection string)
and abandoned on shutdown through `WaitAsync(token)`, which disposes any late-arriving stream.
Exemplar: `src/HybridConnectionClientProvider.cs`.

**H-RES-3 Disposal.** Use `using var` for `TcpClient`, streams and linked `CancellationTokenSource`s. When one
pump finishes, cancel the other and explicitly `Close()` both streams, because `HybridConnectionStream`
reads do not honour cancellation. Then await both pumps.
Exemplar: `ClientProxyConnection.Run`.

**H-RES-4 Graceful shutdown.** SIGTERM/SIGINT (`PosixSignalRegistration`) or a key press cancels one
`CancellationTokenSource`; a second signal falls through to default termination. `ProxyHost.StopAsync`
cancels, stops listeners, and waits up to `ProxyHost.ShutdownTimeout` (5 s) for connections, logging a
Warning on timeout. Logs are flushed in `Program.cs`'s `finally`. The systemd unit's `TimeoutStopSec` must
exceed the drain timeout.

**H-RES-5 A dead accept loop ends the process.** Relay and network faults are handled per connection and never
reach the accept loop, so an accept loop that stops while not shutting down is a local fault (a closed or
disposed listener, a bug). `ClientProxy` logs an Error and reports it (`ProxyHost.AcceptLoopFailed`);
`Program.cs` then stops every proxy and exits 1, and the service manager's restart re-binds every port.
Do not retry or re-bind a single proxy in-process, and do not keep running degraded.

**H-RES-6 Timings are named constants.** Timeouts and back-offs are named, documented constants
(`ProxyHost.ShutdownTimeout`, `ClientProxy.AcceptRetryDelay`), not settings, unless a deployment needs to tune
one. A constant that a deployment must stay consistent with (the drain vs `TimeoutStopSec`) is documented in
the README.

---

## Decisions (owner, 2026-10-22)

The survey's open questions and how they were resolved:

1. **Gitignore gaps** → fixed separately (PR #7); recorded in H-CFG-5.
2. **Cancellation swallowed without checking the token** → only our own cancellation is quiet; library-raised
   cancellations are relay failures (H-ERR-5, H-ERR-3). Code changed.
3. **Telemetry** → logs only, `Type=exec`, deliberately (H-TEL-1).
4. **A proxy whose accept loop dies** → exit 1 and let systemd restart (H-RES-5, H-ERR-1). Code changed.
5. **Serilog misconfiguration** → configuration error, exit 2 (H-ERR-1, H-ERR-2). Code changed.
6. **Identity properties** → enrichers configured in the `Serilog` section, only when a structured sink needs
   them; `{Proxy}` stays one label; `FromLogContext` removed (H-LOG-1, H-LOG-2, H-LOG-9). Code changed.
7. **Stack traces** → none below Fatal (H-LOG-5).
8. **Client IP at Debug** → acceptable (H-LOG-4).
9. **Uncommented empty catches** → every empty catch has a comment (H-ERR-4). Code changed.
10. **Process-wide safety net** → both handlers added (H-ERR-7). Code changed.
11. **Hard-coded timings** → stay constants, now named (H-RES-6). Code changed.
12. **"Unused" identity packages** → the survey's premise was wrong. `Azure.Core`, `Azure.Identity`,
    `Microsoft.IdentityModel.JsonWebTokens` and `System.IdentityModel.Tokens.Jwt` are security pins:
    `Microsoft.Azure.Relay` depends on older versions with known vulnerabilities. They stay, grouped and
    commented in the csproj. Whenever `Microsoft.Azure.Relay` (or another package depending on them) is upgraded,
    try removing them and keep only those still needed for
    `dotnet list package --vulnerable --include-transitive` to report nothing.

## Not yet covered

- **Entra ID authentication.** When `AuthMode=EntraId` lands (in `HybridConnectionProviderFactory`), token
  acquisition needs its own timeout, redaction (tokens, client secrets) and retry rules here.
