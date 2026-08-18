using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The fail-closed, read-only presentation entry point the Fluent layer
/// calls: a transparent projection of the landed internal Liquid wallet
/// surface into the public immutable snapshot types of this namespace.
/// <see cref="LoadAndCaptureBalances"/> is the single public entry point
/// the Fluent/Desktop lifetime layer calls on wallet open — it composes the
/// landed <see cref="LiquidWalletLoadSave.Load"/> in-assembly (the internal
/// <see cref="LiquidWalletState"/> never crosses the assembly boundary) and
/// projects the restored state via <see cref="LiquidWalletUiSnapshot.Capture"/>.
/// <see cref="CaptureBalances"/> is the in-assembly composition point for
/// the WalletWasabi-side wallet-lifetime caller that already holds the
/// loaded state. <see cref="CreateReceiveAddress"/> composes the landed
/// <see cref="LiquidBlindingPublicKey.Create"/> +
/// <see cref="LiquidAddress.FromScriptPubKey"/> + the confidential-only
/// <see cref="LiquidWalletUiReceiveAddress.FromAddress"/> projection. This
/// facade performs no I/O beyond the landed <c>Load</c>, no node
/// connection, no sync, no key derivation, no formatting, and no caching;
/// every rejection surfaces with the landed exception surface — no retry,
/// no fallback, no cached-last-good-value substitution, no empty-snapshot
/// substitution, and no catch-and-rethrow remapping. The key, context,
/// script, and blinding-key spans are caller-supplied
/// <see cref="ReadOnlySpan{T}"/> values that cannot be captured or stored,
/// so the clearing obligation is structural.
/// </summary>
public static class LiquidWalletUiFacade
{
	/// <summary>
	/// Projects the already-loaded <paramref name="state"/> into an
	/// immutable display-ready snapshot. The <paramref name="state"/>
	/// reference is used only for the duration of the call and is never
	/// stored. Throws <see cref="ArgumentException"/> when the state's
	/// pegged asset does not match the manifest's (a wallet is never
	/// presented against the wrong network).
	/// </summary>
	internal static LiquidWalletUiSnapshot CaptureBalances(
		string walletName,
		ElementsPublicNetworkManifest manifest,
		LiquidWalletState state) =>
		LiquidWalletUiSnapshot.Capture(walletName, manifest, state);

	/// <summary>
	/// Derives and projects one confidential receive address from the
	/// caller-supplied next-receive script and blinding public key. The
	/// caller owns the script and blinding-key derivation (key management
	/// is outside this layer). Fail-closed: an empty script, or a
	/// non-33-byte, invalid, or uncompressed blinding key, or a
	/// non-confidential derivation, throws <see cref="ArgumentException"/>;
	/// a malformed or network-mismatched composition surfaces the landed
	/// codec exception.
	/// </summary>
	public static LiquidWalletUiReceiveAddress CreateReceiveAddress(
		ElementsPublicNetworkManifest manifest,
		ReadOnlySpan<byte> scriptPubKey,
		ReadOnlySpan<byte> blindingPublicKey)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		if (scriptPubKey.IsEmpty)
		{
			throw new ArgumentException(
				"A non-empty Liquid receive script is required.",
				nameof(scriptPubKey));
		}

		LiquidBlindingPublicKey blindingKey = LiquidBlindingPublicKey.Create(blindingPublicKey);
		LiquidAddress address = LiquidAddress.FromScriptPubKey(manifest, scriptPubKey, blindingKey);
		return LiquidWalletUiReceiveAddress.FromAddress(address);
	}

	/// <summary>
	/// The single public entry point the Fluent/Desktop lifetime layer
	/// calls on Liquid wallet open: resolves the loaded state via the
	/// landed <see cref="LiquidWalletLoadSave.Load"/> and projects it via
	/// <see cref="LiquidWalletUiSnapshot.Capture"/>. Fail-closed exactly as
	/// the landed <c>Load</c>: a missing file, corrupt frame, wrong key,
	/// wrong context, or revision mismatch surfaces as the landed exception
	/// with no retry, no fallback, and no empty-snapshot substitution. The
	/// loaded state is used only for the projection and is not retained.
	/// </summary>
	public static LiquidWalletUiSnapshot LoadAndCaptureBalances(
		string walletDataDir,
		string walletName,
		ElementsPublicNetworkManifest manifest,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		ulong? expectedBaseRevision = null)
	{
		ArgumentNullException.ThrowIfNull(manifest);

		LiquidWalletLoadSaveResult result = LiquidWalletLoadSave.Load(
			walletDataDir,
			walletName,
			key,
			externalWalletNetworkContext,
			expectedBaseRevision);
		// Load always returns a non-null State; the null-forgiving operator
		// adds no runtime check and no fallback.
		return LiquidWalletUiSnapshot.Capture(walletName, manifest, result.State!);
	}

	/// <summary>
	/// The single public entry point the Fluent layer calls to build an
	/// exact Liquid spend plan: loads the state in-assembly via the landed
	/// <see cref="LiquidWalletLoadSave.Load"/> (no
	/// <c>expectedBaseRevision</c> is passed on this path — the load-time
	/// base-revision fence is not applied here; the caller that needs the
	/// persistence-path fence applies it at its own wallet-open
	/// <c>Load</c>) and delegates to the in-assembly composition point
	/// <see cref="CreateSpendPlan"/>. The loaded state is used only for the
	/// duration of the call and is never stored, returned, or exposed.
	/// Fail-closed exactly as the landed <c>Load</c> and the landed
	/// spend-plan surface: a missing file, corrupt frame, wrong key, wrong
	/// context, stale revision, insufficient balance, oversized plan,
	/// invalid destination, or manifest mismatch surfaces as the landed
	/// exception with no retry, no fallback, no cached-plan substitution,
	/// and no empty-plan substitution.
	/// </summary>
	public static LiquidWalletUiSpendPlan LoadAndCreateSpendPlan(
		string walletDataDir,
		string walletName,
		ElementsPublicNetworkManifest manifest,
		ReadOnlySpan<byte> key,
		ReadOnlySpan<byte> externalWalletNetworkContext,
		IReadOnlyList<string> selectedOutPointHexes,
		string confidentialDestinationAddress,
		string destinationAssetIdHex,
		long destinationAtomicUnits,
		long explicitFeeAtomicUnits,
		ulong? expectedRevision = null)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(selectedOutPointHexes);
		ArgumentNullException.ThrowIfNull(confidentialDestinationAddress);
		ArgumentNullException.ThrowIfNull(destinationAssetIdHex);

		LiquidWalletLoadSaveResult result = LiquidWalletLoadSave.Load(
			walletDataDir,
			walletName,
			key,
			externalWalletNetworkContext);
		// Load always returns a non-null State; the null-forgiving operator
		// adds no runtime check and no fallback.
		return CreateSpendPlan(
			walletName,
			manifest,
			result.State!,
			selectedOutPointHexes,
			confidentialDestinationAddress,
			destinationAssetIdHex,
			destinationAtomicUnits,
			explicitFeeAtomicUnits,
			expectedRevision);
	}

	/// <summary>
	/// The in-assembly composition point for the WalletWasabi-side
	/// wallet-lifetime caller that already holds the loaded
	/// <paramref name="state"/> from its own landed <c>Load</c>. It is
	/// <see langword="internal"/> — not <see langword="public"/> — for
	/// exactly the reason the landed <see cref="CaptureBalances"/> is
	/// internal: it names the internal <see cref="LiquidWalletState"/>, and
	/// a public method on this public facade class may not declare a
	/// parameter of a less accessible type (CS0051). Composes the landed
	/// parse/validate/create chain — <see cref="LiquidAddress.Parse"/>,
	/// <see cref="LiquidAssetId.ParseRpcHex"/>,
	/// <see cref="LiquidAssetAmount.Create"/>,
	/// <see cref="LiquidSuppliedConfidentialDestination.Create"/>,
	/// <see cref="LiquidSuppliedConfidentialDestinationBatch.Create"/>,
	/// <see cref="LiquidWalletState.CreateExactOrdinaryWalletSpendPlan"/> —
	/// and projects the resulting plan via
	/// <see cref="LiquidWalletUiSpendPlan.FromPlan"/>. The
	/// <paramref name="state"/> reference is used only for the duration of
	/// the call and is never stored. The revision fence is caller-supplied,
	/// never a self-fence: when <paramref name="expectedRevision"/> has a
	/// value it is passed straight through to the landed revision fence (a
	/// stale caller expectation throws
	/// <see cref="InvalidOperationException"/>); when it is null the fence
	/// is explicitly not applied on this path (the freshly loaded state's
	/// own current revision always passes by construction), and the real
	/// fence on the load path is the load-time <c>expectedBaseRevision</c>
	/// fence of the landed <see cref="LiquidWalletLoadSave.Load"/>, which
	/// this path does not re-apply. Fail-closed exactly as the landed
	/// surface: no retry, no fallback, no cached-plan substitution, and no
	/// catch-and-rethrow remapping.
	/// </summary>
	internal static LiquidWalletUiSpendPlan CreateSpendPlan(
		string walletName,
		ElementsPublicNetworkManifest manifest,
		LiquidWalletState state,
		IReadOnlyList<string> selectedOutPointHexes,
		string confidentialDestinationAddress,
		string destinationAssetIdHex,
		long destinationAtomicUnits,
		long explicitFeeAtomicUnits,
		ulong? expectedRevision = null)
	{
		ArgumentNullException.ThrowIfNull(walletName);
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(selectedOutPointHexes);
		ArgumentNullException.ThrowIfNull(confidentialDestinationAddress);
		ArgumentNullException.ThrowIfNull(destinationAssetIdHex);

		if (selectedOutPointHexes.Count == 0)
		{
			throw new ArgumentException(
				"A Liquid spend plan requires at least one selected outpoint.",
				nameof(selectedOutPointHexes));
		}
		if (destinationAtomicUnits <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(destinationAtomicUnits),
				"A positive Liquid destination amount is required.");
		}
		if (explicitFeeAtomicUnits <= 0)
		{
			throw new ArgumentOutOfRangeException(
				nameof(explicitFeeAtomicUnits),
				"A positive Liquid explicit fee is required.");
		}

		// Decode each outpoint hex string to consensus bytes: exactly 72
		// hexadecimal characters (the 36-byte LiquidOutPoint.ConsensusByteLength
		// encoding — the 32-byte transaction id followed by the 4-byte
		// little-endian output index) via Convert.FromHexString per element,
		// fail-closed ArgumentException on malformed hex, then the landed
		// ParseSpendableConsensusBytes.
		var selectedOutPoints = new LiquidOutPoint[selectedOutPointHexes.Count];
		for (int index = 0; index < selectedOutPoints.Length; index++)
		{
			string outPointHex = selectedOutPointHexes[index];
			byte[] consensusBytes;
			try
			{
				consensusBytes = Convert.FromHexString(
					outPointHex ?? throw new ArgumentException(
						"A Liquid selected outpoint hex string cannot be null.",
						nameof(selectedOutPointHexes)));
			}
			catch (FormatException exception)
			{
				throw new ArgumentException(
					"A Liquid selected outpoint must be exactly 72 hexadecimal characters (the 36-byte consensus encoding).",
					nameof(selectedOutPointHexes),
					exception);
			}

			selectedOutPoints[index] = LiquidOutPoint.ParseSpendableConsensusBytes(
				consensusBytes,
				nameof(selectedOutPointHexes));
		}

		LiquidAddress address = LiquidAddress.Parse(manifest, confidentialDestinationAddress);
		LiquidAssetId assetId = LiquidAssetId.ParseRpcHex(destinationAssetIdHex);
		LiquidAssetAmount amount = LiquidAssetAmount.Create(
			assetId,
			LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId),
			destinationAtomicUnits);
		LiquidSuppliedConfidentialDestination destination =
			LiquidSuppliedConfidentialDestination.Create(
				manifest,
				address,
				assetId,
				amount,
				LiquidWalletLabelSet.Empty);
		LiquidSuppliedConfidentialDestinationBatch batch =
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]);
		LiquidAssetAmount explicitFee = LiquidAssetAmount.Create(
			LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId),
			LiquidAssetId.ParseRpcHex(manifest.PeggedAssetId),
			explicitFeeAtomicUnits);

		LiquidOrdinaryWalletExactSpendPlan plan = state.CreateExactOrdinaryWalletSpendPlan(
			expectedRevision ?? state.Revision,
			selectedOutPoints,
			batch,
			explicitFee);
		return LiquidWalletUiSpendPlan.FromPlan(walletName, manifest, plan);
	}
}
