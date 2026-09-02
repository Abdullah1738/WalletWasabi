using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Liquid.Application;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.Models.Wallets.Liquid;

/// <summary>
/// The application-lifetime owner of the single Liquid testnet
/// <see cref="LiquidWalletApplicationClient"/> used by the Fluent layer, and
/// the minimal, explicit, fail-closed testnet profile plumbing that lets an
/// opened wallet refresh. It mirrors the headless harness
/// (<c>tmp/testnet-demo-20260829T170535Z/harness/Program.cs</c>) exactly:
/// the Elements RPC profile JSON is staged under a Liquid application-data
/// directory together with the loopback cookie, and the wallet
/// <c>.json</c>/<c>.lwwal</c> files live under a Liquid wallet directory.
/// Every wallet-core call is the public
/// <see cref="LiquidWalletApplicationClient"/> / <see cref="KeyManager"/>
/// surface the harness uses — no new wallet-core or native calls. The
/// endpoint, cookie source, and profile name come from environment
/// overrides with the reviewed testnet defaults; the session is inert (no
/// client created) until the first wallet open, and any rejection from the
/// landed primitives surfaces as-is with no fallback.
/// </summary>
[AppLifetime]
public sealed class LiquidWalletSession : IAsyncDisposable
{
	// Reviewed Liquid testnet bindings (the manifest the harness drives).
	private const string DefaultProfileName = "testnet-loopback";
	private const string DefaultEndpoint = "http://127.0.0.1:18891";

	private readonly string _applicationDataDirectory;
	private readonly string _liquidWalletDirectory;
	private readonly SemaphoreSlim _clientGate = new(1, 1);
	private readonly Func<LiquidWalletSession, string, string, string, CancellationToken, Task<LiquidWalletModel>> _openCore;
	private LiquidWalletApplicationClient? _client;

	public LiquidWalletSession(string applicationDataDirectory, string liquidWalletDirectory)
		: this(applicationDataDirectory, liquidWalletDirectory, openCore: null)
	{
	}

	/// <summary>
	/// Test seam: <paramref name="openCore"/> replaces the production open body
	/// (client acquisition, authenticated open, refresh-on-open) while keeping
	/// the surrounding resilience wrapper — the transient node-generation retry
	/// — under test. Production passes <see langword="null"/> and uses
	/// <see cref="OpenWalletCoreAsync"/>.
	/// </summary>
	internal LiquidWalletSession(
		string applicationDataDirectory,
		string liquidWalletDirectory,
		Func<LiquidWalletSession, string, string, string, CancellationToken, Task<LiquidWalletModel>>? openCore)
	{
		ArgumentException.ThrowIfNullOrEmpty(applicationDataDirectory);
		ArgumentException.ThrowIfNullOrEmpty(liquidWalletDirectory);
		_applicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
		_liquidWalletDirectory = Path.GetFullPath(liquidWalletDirectory);
		_openCore = openCore ?? OpenWalletCoreAsync;
	}

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;

	private static string ProfileName =>
		Environment.GetEnvironmentVariable("WASABI_LIQUID_PROFILE") is { Length: > 0 } value
			? value.Trim()
			: DefaultProfileName;

	private static string Endpoint =>
		Environment.GetEnvironmentVariable("WASABI_LIQUID_ENDPOINT") is { Length: > 0 } value
			? value.Trim()
			: DefaultEndpoint;

	private static string CookieSource =>
		Environment.GetEnvironmentVariable("WASABI_LIQUID_COOKIE") is { Length: > 0 } value
			? value.Trim()
			: DefaultCookieSource.Value;

	// Resolved lazily so a missing demo-cookie file fails closed only when the env
	// override is absent AND the loopback endpoint is actually contacted, not at class load.
	private static readonly Lazy<string> DefaultCookieSource = new(() =>
	{
		string path = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"wasabi-liquid", "WalletWasabi", "tmp", "testnet-demo-20260829T170535Z",
			"node", "liquidtestnet", ".cookie");
		if (!File.Exists(path))
		{
			throw new InvalidOperationException(
				"The reviewed Liquid testnet demo cookie is absent. Set WASABI_LIQUID_COOKIE " +
				"to an explicit cookie path for this node.");
		}
		return path;
	});

	/// <summary>The canonical Liquid wallet directory (<c>.json</c> + <c>.lwwal</c>).</summary>
	public string LiquidWalletDirectory => _liquidWalletDirectory;

	/// <summary>The canonical file path a Liquid wallet with this name is stored at.</summary>
	public string GetWalletFilePath(string walletName) =>
		Path.Combine(_liquidWalletDirectory, walletName + ".json");

	/// <summary>True when a wallet with this name is already registered under the Liquid wallet directory.</summary>
	public bool WalletExists(string walletName) => File.Exists(GetWalletFilePath(walletName));

	/// <summary>
	/// True when at least one Liquid wallet file exists on disk. This is the
	/// Liquid analogue of the BTC WalletManager.HasWallet used by the shell's
	/// out-of-box (welcome/backdrop) logic — the Liquid repository itself is
	/// runtime-only and does not scan the directory at startup.
	/// </summary>
	public bool HasAnyWalletFile() =>
		Directory.Exists(_liquidWalletDirectory) &&
		Directory.EnumerateFiles(_liquidWalletDirectory, "*.json").Any();

	/// <summary>
	/// Creates a fresh Liquid testnet wallet file (name + password + recovery
	/// words) against <see cref="Network.TestNet"/>, exactly as the harness
	/// does for its first-run wallet. Returns the generated recovery words for
	/// the backup display; the wallet is not opened here. Refuses to overwrite
	/// an existing file.
	/// </summary>
	public Mnemonic CreateWalletFile(string walletName, string password)
	{
		string walletFile = GetWalletFilePath(walletName);
		if (File.Exists(walletFile))
		{
			throw new InvalidOperationException($"A Liquid wallet file already exists at '{walletFile}'; refusing to overwrite it.");
		}

		Directory.CreateDirectory(_liquidWalletDirectory);
		// The testnet manifest derives its descriptor network from NBitcoin's
		// TestNet (the landed LiquidAuthenticatedWalletStateOwner does the same
		// for any non-mainnet manifest), so the wallet file is created against
		// Network.TestNet.
		KeyManager.CreateNew(out Mnemonic mnemonic, password, Network.TestNet, walletFile);
		SetOwnerOnly(walletFile);
		return mnemonic;
	}

	/// <summary>
	/// Creates a fresh Liquid testnet wallet file from an already-generated
	/// mnemonic (used when the words were produced up-front for the backup
	/// display). Refuses to overwrite an existing file.
	/// </summary>
	public void CreateWalletFile(string walletName, Mnemonic mnemonic, string password)
	{
		ArgumentNullException.ThrowIfNull(mnemonic);
		string walletFile = GetWalletFilePath(walletName);
		if (File.Exists(walletFile))
		{
			throw new InvalidOperationException($"A Liquid wallet file already exists at '{walletFile}'; refusing to overwrite it.");
		}

		Directory.CreateDirectory(_liquidWalletDirectory);
		KeyManager.CreateNew(mnemonic, password, Network.TestNet, walletFile);
		SetOwnerOnly(walletFile);
	}

	/// <summary>
	/// Restores a Liquid testnet wallet file from recovery words against
	/// <see cref="Network.TestNet"/> using the same SegWit account key path the
	/// open path derives. The wallet is not opened here. Refuses to overwrite
	/// an existing file.
	/// </summary>
	public void RecoverWalletFile(string walletName, Mnemonic mnemonic, string password)
	{
		ArgumentNullException.ThrowIfNull(mnemonic);
		string walletFile = GetWalletFilePath(walletName);
		if (File.Exists(walletFile))
		{
			throw new InvalidOperationException($"A Liquid wallet file already exists at '{walletFile}'; refusing to overwrite it.");
		}

		Directory.CreateDirectory(_liquidWalletDirectory);
		KeyPath accountKeyPath = KeyManager.GetAccountKeyPath(Network.TestNet, ScriptPubKeyType.Segwit);
		KeyManager.Recover(mnemonic, password, Network.TestNet, accountKeyPath, filePath: walletFile);
		SetOwnerOnly(walletFile);
	}

	// The transient node-generation race retries at most this many times after
	// the first attempt before the error surfaces.
	private const int TransientGenerationMaxRetries = 2;

	// Small backoff between transient node-generation retries; a testnet block
	// lands well outside this window, so the retry re-acquires against the new
	// tip instead of racing the same landing block.
	private static readonly TimeSpan TransientGenerationRetryDelay = TimeSpan.FromMilliseconds(150);

	/// <summary>
	/// Opens an existing Liquid wallet file by password, issues one manual
	/// refresh against the node so a funded wallet shows its balance, and
	/// returns a populated <see cref="LiquidWalletModel"/>. Fail-closed: a
	/// missing file, wrong password, unreachable node, or any landed rejection
	/// surfaces as-is. The single transient exception is the acquisition-window
	/// node-generation race (a testnet block landing mid-acquisition): the open
	/// is retried with a small backoff, and a failed attempt leaves no session
	/// registered, so an immediate retry — automatic or user-driven — is never
	/// blocked by a stuck open guard.
	/// </summary>
	public Task<LiquidWalletModel> OpenWalletAsync(
		string walletName,
		string walletFilePath,
		string password,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrEmpty(walletName);
		ArgumentException.ThrowIfNullOrEmpty(walletFilePath);
		ArgumentNullException.ThrowIfNull(password);

		return RunWithTransientGenerationRetryAsync(
			() => _openCore(this, walletName, walletFilePath, password, cancellationToken),
			cancellationToken);
	}

	/// <summary>
	/// Runs an open-path acquisition, retrying only the transient
	/// node-generation race (an <see cref="InvalidOperationException"/> or
	/// <see cref="WalletWasabi.Liquid.Rpc.ElementsRpcException"/> whose message
	/// carries the acquisition-window fence text) with a small backoff. Every
	/// other rejection — wrong password, unreachable node, any landed
	/// non-transient failure — surfaces on the first attempt, unretried.
	/// </summary>
	private static async Task<LiquidWalletModel> RunWithTransientGenerationRetryAsync(
		Func<Task<LiquidWalletModel>> attempt,
		CancellationToken cancellationToken)
	{
		for (int retriesUsed = 0; ; retriesUsed++)
		{
			try
			{
				return await attempt().ConfigureAwait(false);
			}
			catch (Exception ex) when (retriesUsed < TransientGenerationMaxRetries && IsTransientNodeGenerationRace(ex))
			{
				await Task.Delay(TransientGenerationRetryDelay, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private static bool IsTransientNodeGenerationRace(Exception exception) =>
		exception is InvalidOperationException or WalletWasabi.Liquid.Rpc.ElementsRpcException
		&& exception.Message.Contains("node generation changed during the acquisition", StringComparison.Ordinal);

	/// <summary>
	/// The production open body: acquires the application client, opens the
	/// authenticated session, and refreshes once so a funded wallet presents
	/// its current balance and retained history.
	/// </summary>
	private static async Task<LiquidWalletModel> OpenWalletCoreAsync(
		LiquidWalletSession session,
		string walletName,
		string walletFilePath,
		string password,
		CancellationToken cancellationToken)
	{
		LiquidWalletApplicationClient client = await session.GetOrCreateClientAsync(cancellationToken).ConfigureAwait(false);

		char[] passwordChars = password.ToCharArray();
		try
		{
			LiquidWalletRuntimeHandoff handoff;
			using (LiquidWalletOpenAuthorization authorization = client.CreateOpenAuthorization(passwordChars))
			{
				var request = new LiquidWalletOpenRequest(walletName, walletFilePath, ProfileName);
				handoff = await client.OpenAsync(request, authorization, cancellationToken).ConfigureAwait(false);
			}

			// Refresh once on open so an already-funded wallet presents its
			// current balance; the refreshed handoff (when published) replaces
			// the open-time one.
			await client.RefreshCommand(
				new LiquidWalletUiRefreshRequest(walletName, LiquidWalletUiRefreshTrigger.Manual, null),
				cancellationToken).ConfigureAwait(false);

			LiquidWalletRuntimeHandoff current = client.CurrentHandoff ?? handoff;
			ElementsPublicNetworkManifest manifest = ElementsPublicNetworkManifest.GetByManifestId(current.NetworkManifestId);

			var model = new LiquidWalletModel(
				current.CanonicalWalletId,
				manifest,
				current.Balances,
				current.ReceiveMaterial.NextReceiveScriptPubKey,
				current.ReceiveMaterial.NextReceiveBlindingPublicKey,
				current.ReceiveMaterial.NextReceiveLabels,
				session.SetNextReceiveLabelsAsync);

			// Feed the already-produced history into the model so a funded
			// wallet presents its retained history instead of the "not
			// available" state. The handoff guarantees
			// History.Revision == Balances.Revision, so the model's pairing
			// fence accepts it.
			model.RefreshHistory(current.History);
			return model;
		}
		finally
		{
			Array.Clear(passwordChars);
		}
	}

	/// <summary>
	/// The single narrow non-secret send-execution surface the Fluent send flow
	/// is wired with (MANAGED-WALLET-UI-SEND-EXECUTE-001, V2 section 8): one
	/// <see cref="Func{T1,T2,TResult}"/> over the public request/result types.
	/// It delegates to the single application client's
	/// <see cref="LiquidWalletApplicationClient.SendCommand"/> for the wallet the
	/// request names — the same open authenticated session the wallet was opened
	/// with (the landed command service resolves that session by
	/// <see cref="LiquidWalletUiSendExecutionRequest.WalletName"/> and rejects
	/// any other wallet fail-closed) — after replacing the request's
	/// previous-transaction-id dependency rows with the rows derived from the
	/// open session's current runtime handoff, mirroring the headless harness
	/// send phase exactly: one row per selected outpoint carrying the outpoint's
	/// own transaction id (<c>new[] { selected.TransactionIdHex }</c>), looked
	/// up in <see cref="LiquidWalletRuntimeHandoff.SelectableOutputs"/> by
	/// selection id; an unselected outpoint leaves the row untouched (the
	/// landed funding composition rejects a null row fail-closed when one is
	/// required). Key management stays in this lifetime layer: the delegate
	/// never receives or returns key material, and the view model never holds
	/// keys. Fail-closed: any rejection from the landed send surface surfaces
	/// as-is — no fabricated success, no retry, no fallback.
	/// </summary>
	public async Task<LiquidWalletUiSendExecutionResult> ExecuteSendAsync(
		LiquidWalletUiSendExecutionRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		LiquidWalletApplicationClient client = await GetOrCreateClientAsync(cancellationToken).ConfigureAwait(false);
		LiquidWalletUiSendExecutionRequest authorized = WithSessionPreviousTransactionRows(client, request);
		return await client.SendCommand(authorized, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The single narrow non-secret receive-label write surface the Fluent
	/// receive flow is wired with: one <see cref="Func{T1,T2,TResult}"/> over
	/// the public request type. It delegates to the single application
	/// client's <see cref="LiquidWalletApplicationClient.SetNextReceiveLabelsAsync"/>
	/// for the wallet the request names — the same open authenticated session
	/// the wallet was opened with — persisting the durable label set through
	/// the landed, generation-fenced receive-label command service (NOT a
	/// process-local dictionary). Key management stays in this lifetime
	/// layer: the delegate never receives or returns key material, and the
	/// view model never holds keys. Fail-closed: any rejection from the
	/// landed surface surfaces as-is.
	/// </summary>
	public async Task SetNextReceiveLabelsAsync(
		LiquidWalletUiSetReceiveLabelsRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		LiquidWalletApplicationClient client = await GetOrCreateClientAsync(cancellationToken).ConfigureAwait(false);
		await client.SetNextReceiveLabelsAsync(request, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Rebuilds the send request with the previous-transaction-id dependency
	/// rows sourced from the open session's current selectable outputs (the
	/// same source the harness send phase uses), one row per selected outpoint
	/// in order. Rows whose outpoint is not in the session's selectable set
	/// pass through unchanged; everything else in the request is preserved
	/// verbatim.
	/// </summary>
	private static LiquidWalletUiSendExecutionRequest WithSessionPreviousTransactionRows(
		LiquidWalletApplicationClient client,
		LiquidWalletUiSendExecutionRequest request)
	{
		LiquidWalletRuntimeHandoff? handoff = client.CurrentHandoff;
		if (handoff is null || !StringComparer.Ordinal.Equals(handoff.CanonicalWalletId, request.WalletName))
		{
			return request;
		}

		Dictionary<string, string> transactionIdBySelectionId = new(StringComparer.Ordinal);
		foreach (LiquidWalletUiSelectableOutput output in handoff.SelectableOutputs.Outputs)
		{
			transactionIdBySelectionId[output.SelectionId] = output.TransactionIdHex;
		}

		IReadOnlyList<string> selected = request.SelectedOutPointHexes;
		IReadOnlyList<IReadOnlyList<string>?> requestedRows = request.PreviousTransactionIdsBySelectedInput;
		var rows = new IReadOnlyList<string>?[selected.Count];
		for (int index = 0; index < rows.Length; index++)
		{
			rows[index] = transactionIdBySelectionId.TryGetValue(selected[index], out string? transactionIdHex)
				? new[] { transactionIdHex }
				: requestedRows[index];
		}

		return new LiquidWalletUiSendExecutionRequest(
			request.WalletName,
			selected,
			request.ConfidentialDestinationAddress,
			request.DestinationAssetIdHex,
			request.DestinationAtomicUnits,
			request.ExplicitFeeAtomicUnits,
			request.ExpectedRevision,
			rows);
	}

	/// <summary>
	/// Lazily stages the testnet profile (cookie + profile JSON under the
	/// application-data directory) and creates the single application client.
	/// Serialized so a slow first open cannot race a second one into a double
	/// create.
	/// </summary>
	private async Task<LiquidWalletApplicationClient> GetOrCreateClientAsync(CancellationToken cancellationToken)
	{
		if (_client is { } existing)
		{
			return existing;
		}

		await _clientGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_client is { } raced)
			{
				return raced;
			}

			PrepareDirectoriesAndProfile();
			var options = new LiquidWalletApplicationOptions(
				_applicationDataDirectory,
				_liquidWalletDirectory,
				Manifest.ManifestId);
			_client = LiquidWalletApplicationClient.Create(options);
			return _client;
		}
		finally
		{
			_clientGate.Release();
		}
	}

	/// <summary>
	/// Mirrors the harness <c>PrepareDirectoriesAndProfile</c>: creates the
	/// application-data and wallet directories, stages the loopback cookie under
	/// the application-data directory (the profile contract requires the cookie
	/// to live beneath it), and writes the reviewed v1 profile JSON. Loopback
	/// RPC only.
	/// </summary>
	private void PrepareDirectoriesAndProfile()
	{
		Directory.CreateDirectory(_applicationDataDirectory);
		Directory.CreateDirectory(_liquidWalletDirectory);
		string profileDirectory = Path.Combine(_applicationDataDirectory, "liquid-rpc-profiles");
		Directory.CreateDirectory(profileDirectory);

		string stagedCookie = Path.Combine(_applicationDataDirectory, "rpc.cookie");
		File.Copy(CookieSource, stagedCookie, overwrite: true);
		SetOwnerOnly(stagedCookie);

		string profileFile = Path.Combine(profileDirectory, ProfileName + ".json");
		string profileJson =
			"{\"schema\":\"walletwasabi-liquid-rpc-profile/v1\",\"name\":\"" + ProfileName +
			"\",\"endpoint\":\"" + Endpoint +
			"\",\"cookieFile\":\"" + stagedCookie.Replace("\\", "\\\\") +
			"\",\"network\":\"" + Manifest.ChainRpcName +
			"\",\"manifest\":\"" + Manifest.ManifestId +
			"\",\"connectTimeoutMs\":5000,\"requestTimeoutMs\":60000}";
		File.WriteAllText(profileFile, profileJson);
		SetOwnerOnly(profileFile);
	}

	private static void SetOwnerOnly(string path)
	{
		if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_client is { } client)
		{
			await client.DisposeAsync().ConfigureAwait(false);
		}

		_clientGate.Dispose();
	}
}
