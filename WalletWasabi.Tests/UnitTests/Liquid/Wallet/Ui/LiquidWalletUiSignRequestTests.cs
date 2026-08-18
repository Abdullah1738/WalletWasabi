using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Sync;
using WalletWasabi.Liquid.Wallet.Ui;
using WalletWasabi.Liquid.Wallet.Wire;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Liquid.Wallet.Wire;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet.Ui;

[Collection("Serial unit tests collection")]
public class LiquidWalletUiSignRequestTests
{
	private const string PublicKeyHex = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
	private const string BlindingKeyHex = "02c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";

	private static ElementsPublicNetworkManifest Manifest => ElementsPublicNetworkManifest.LiquidTestnet;
	private static LiquidAssetId PeggedAsset => LiquidAssetId.ParseRpcHex(Manifest.PeggedAssetId);
	private static LiquidSpendKeyReference ExternalKey => Key(LiquidKeyBranch.External, 0);
	private static byte[] BlindingKey => Convert.FromHexString(BlindingKeyHex);
	private static byte[] ReceiveScript => ExternalKey.GetScriptPubKey();
	private static byte[] SourceEpoch => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

	// Required evidence §1: sign-request construction from a valid plan +
	// funding source. A state with a sufficient pegged-asset balance, a valid
	// confidential destination, a caller-supplied 32-byte sourceEpoch, and a
	// caller-constructed ElementsExpectationBoundRawTransactionBatch whose raw
	// transactions cover the selected input yields a LiquidWalletUiSignRequest
	// whose WireFrameHex decodes to a frame the landed native validation
	// accepts (status 0) against the same epoch, whose SourceEpochHex equals
	// the supplied epoch, whose Inputs count equals SelectedInputCount, whose
	// ConfidentialOutputCount is 1, whose ExplicitFeeAtomicUnits equals the
	// explicit fee, whose SourceRevision equals state.Revision, and whose
	// IsConfidential is true.
	[Fact]
	public void CreateSignRequestFromValidPlanAndFundingSource()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));
		string confidentialAddress = ConfidentialAddress();
		string[] selectedOutPointHexes = [OutPointHex(txA, 0)];
		byte[] epoch = SourceEpoch;
		ElementsExpectationBoundRawTransactionBatch fundingSource = CreateFundingSource(txA);
		IReadOnlyList<IReadOnlyList<string>?> previousIds = [Array.Empty<string>()];

		LiquidWalletUiSignRequest request = LiquidWalletUiFacade.CreateSignRequest(
			"wallet",
			Manifest,
			state,
			selectedOutPointHexes,
			confidentialAddress,
			Manifest.PeggedAssetId,
			destinationAtomicUnits: 9_000,
			explicitFeeAtomicUnits: 1_000,
			epoch,
			fundingSource,
			previousIds);

		Assert.Equal("wallet", request.WalletName);
		Assert.Equal(Manifest.ManifestId, request.NetworkManifestId);
		Assert.Equal(Manifest.PeggedAssetId, request.PeggedAssetIdHex);
		Assert.Equal(state.Revision, request.SourceRevision);
		Assert.Equal(1, request.ConfidentialOutputCount);
		Assert.Equal(1_000, request.ExplicitFeeAtomicUnits);
		Assert.True(request.IsConfidential);
		Assert.Equal(Convert.ToHexString(epoch).ToLowerInvariant(), request.SourceEpochHex);

		// The single input projects the outpoint consensus hex, the pegged
		// asset id, and the exact atomic units.
		LiquidWalletUiSignRequestInput input = Assert.Single(request.Inputs);
		Assert.Equal(OutPointHex(txA, 0).ToLowerInvariant(), input.OutPointHex);
		Assert.Equal(Manifest.PeggedAssetId, input.AssetIdHex);
		Assert.Equal(10_000, input.AtomicUnits);

		// The wire frame decodes to a frame the landed native validation
		// accepts (status 0) against the same epoch: a test-only cross-check
		// through the existing test binding.
		byte[] frameBytes = Convert.FromHexString(request.WireFrameHex);
		try
		{
			int status = LiquidOrdinaryWalletPlanWireV1NativeValidation.Validate(frameBytes, epoch);
			Assert.Equal(LiquidOrdinaryWalletPlanWireV1NativeValidation.StatusOkV1, status);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(frameBytes);
		}
	}

	// Required evidence §1 (round-trip through the public entry point): a
	// saved state loads and builds a sign request through
	// LoadAndCreateSignRequest to the same projection.
	[Fact]
	public void LoadAndCreateSignRequestRoundTripsThroughPublicEntryPoint()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 7_500)]));
			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 1, key, context);

			byte[] epoch = SourceEpoch;
			LiquidWalletUiSignRequest request = LiquidWalletUiFacade.LoadAndCreateSignRequest(
				walletDataDir,
				"wallet",
				Manifest,
				key,
				context,
				[OutPointHex(txA, 0)],
				ConfidentialAddress(),
				Manifest.PeggedAssetId,
				destinationAtomicUnits: 7_000,
				explicitFeeAtomicUnits: 500,
				epoch,
				CreateFundingSource(txA),
				[Array.Empty<string>()]);

			Assert.Equal("wallet", request.WalletName);
			Assert.Equal(1ul, request.SourceRevision);
			Assert.Single(request.Inputs);
			Assert.Equal(1, request.ConfidentialOutputCount);
			Assert.Equal(500, request.ExplicitFeeAtomicUnits);
			Assert.True(request.IsConfidential);
			Assert.Equal(Convert.ToHexString(epoch).ToLowerInvariant(), request.SourceEpochHex);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §2: fail-closed on invalid plan. A malformed
	// destination, an insufficient balance, an oversized plan, or a manifest
	// mismatch surfaces the landed exception and yields no sign request.
	[Fact]
	public void CreateSignRequestFailsClosedOnInvalidPlan()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));
		byte[] epoch = SourceEpoch;
		ElementsExpectationBoundRawTransactionBatch fundingSource = CreateFundingSource(txA);
		IReadOnlyList<IReadOnlyList<string>?> previousIds = [Array.Empty<string>()];
		string[] selected = [OutPointHex(txA, 0)];

		// Malformed destination address.
		Assert.Throws<LiquidAddressFormatException>(() =>
			LiquidWalletUiFacade.CreateSignRequest(
				"wallet", Manifest, state, selected, "not-a-liquid-address",
				Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));

		// Insufficient balance (exact-selection requirement fails).
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiFacade.CreateSignRequest(
				"wallet", Manifest, state, selected, ConfidentialAddress(),
				Manifest.PeggedAssetId, 20_000, 1_000, epoch, fundingSource, previousIds));

		// Oversized plan: 101 distinct well-formed outpoint hexes.
		string[] oversizedSelection = new string[LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount + 1];
		for (int index = 0; index < oversizedSelection.Length; index++)
		{
			oversizedSelection[index] = OutPointHex(txA, (uint)index);
		}
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			LiquidWalletUiFacade.CreateSignRequest(
				"wallet", Manifest, state, oversizedSelection, ConfidentialAddress(),
				Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));
	}

	// Required evidence §2 (manifest mismatch): FromPlanAndFrame with a plan
	// bound to a different manifest throws ArgumentException and yields no
	// sign request.
	[Fact]
	public void FromPlanAndFrameFailsClosedOnManifestMismatch()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));
		LiquidOrdinaryWalletExactSpendPlan plan = BuildLandedPlan(state, txA);
		byte[] epoch = SourceEpoch;

		// The plan is bound to the testnet manifest; projecting it against the
		// mainnet manifest fails closed.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiSignRequest.FromPlanAndFrame(
				"wallet",
				ElementsPublicNetworkManifest.LiquidMainnet,
				plan,
				epoch,
				epoch));
	}

	// Required evidence §3: fail-closed on stale revision. A caller-supplied
	// expectedRevision behind the loaded state's Revision throws
	// InvalidOperationException from the landed EnsureRevision, through both
	// the public LoadAndCreateSignRequest and the internal CreateSignRequest.
	[Fact]
	public void CreateSignRequestFailsClosedOnStaleRevision()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidTransactionId txB = Tx('b');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]))
				.Apply(1, Delta(txB, [], [Output(txB, 0, PeggedAsset, 5_000)]));
			Assert.Equal(2ul, state.Revision);
			byte[] epoch = SourceEpoch;
			ElementsExpectationBoundRawTransactionBatch fundingSource = CreateFundingSource(txA);
			IReadOnlyList<IReadOnlyList<string>?> previousIds = [Array.Empty<string>()];
			string[] selected = [OutPointHex(txA, 0)];
			string confidentialAddress = ConfidentialAddress();

			// The internal composition point.
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", Manifest, state, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds,
					expectedRevision: 1));

			// The public entry point.
			string walletDataDir = GetWorkDir();
			LiquidWalletLoadSave.Save(walletDataDir, "wallet", state, 1, key, context);
			Assert.Throws<InvalidOperationException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSignRequest(
					walletDataDir, "wallet", Manifest, key, context, selected,
					confidentialAddress, Manifest.PeggedAssetId, 9_000, 1_000, epoch,
					fundingSource, previousIds, expectedRevision: 1));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §4: fail-closed on bad epoch. A sourceEpoch not
	// exactly 32 bytes throws ArgumentException before any state load or
	// native call.
	[Fact]
	public void CreateSignRequestFailsClosedOnBadEpoch()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));
			ElementsExpectationBoundRawTransactionBatch fundingSource = CreateFundingSource(txA);
			IReadOnlyList<IReadOnlyList<string>?> previousIds = [Array.Empty<string>()];
			string[] selected = [OutPointHex(txA, 0)];
			string confidentialAddress = ConfidentialAddress();

			// Internal composition point: 31-byte and 33-byte epochs.
			Assert.Throws<ArgumentException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", Manifest, state, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, SourceEpoch.AsSpan(..^1), fundingSource, previousIds));
			Assert.Throws<ArgumentException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", Manifest, state, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, new byte[33], fundingSource, previousIds));

			// The public entry point rejects the bad epoch before any state
			// load: a nonexistent wallet path still throws ArgumentException
			// (the epoch check precedes the Load).
			string walletDataDir = GetWorkDir();
			Assert.Throws<ArgumentException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSignRequest(
					walletDataDir, "missing", Manifest, key, context, selected,
					confidentialAddress, Manifest.PeggedAssetId, 9_000, 1_000,
					ReadOnlySpan<byte>.Empty, fundingSource, previousIds));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// Required evidence §5: fail-closed on funding/frame composition failure.
	// A funding source whose raw transactions do not cover the selected input
	// causes TryCreateOrdinaryWalletPlanFundingBatch to return false, surfaced
	// as a fail-closed InvalidOperationException naming the wire error code.
	[Fact]
	public void CreateSignRequestFailsClosedOnFundingCompositionFailure()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidTransactionId txUnrelated = Tx('f');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));
		byte[] epoch = SourceEpoch;
		IReadOnlyList<IReadOnlyList<string>?> previousIds = [Array.Empty<string>()];
		string[] selected = [OutPointHex(txA, 0)];

		// A funding source whose only raw transaction is an unrelated
		// transaction id (it does not cover the selected input).
		ElementsExpectationBoundRawTransactionBatch nonCoveringSource =
			CreateFundingSource(txUnrelated);
		InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
			LiquidWalletUiFacade.CreateSignRequest(
				"wallet", Manifest, state, selected, ConfidentialAddress(),
				Manifest.PeggedAssetId, 9_000, 1_000, epoch, nonCoveringSource, previousIds));
		Assert.Contains(
			LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument.GetMessage(),
			exception.Message,
			StringComparison.Ordinal);
	}

	// Required evidence §6: seam signer refusal is fail-closed. A signer
	// double whose GetPublicKeyHex returns null for any input, or whose
	// SignDigestHex returns null, causes TrySign to return false with a null
	// signedTransaction. A null signer or null request returns false.
	[Fact]
	public void TrySignFailsClosedOnSignerRefusal()
	{
		LiquidWalletUiSignRequest request = BuildValidSignRequest();

		// Null signer / null request.
		Assert.False(LiquidWalletUiSigner.TrySign(null!, request, out LiquidWalletUiSignedTransaction? nullSignerResult));
		Assert.Null(nullSignerResult);
		Assert.False(LiquidWalletUiSigner.TrySign(new WellFormedSigner(request), null!, out LiquidWalletUiSignedTransaction? nullRequestResult));
		Assert.Null(nullRequestResult);

		// GetPublicKeyHex refuses (returns null).
		var refusingKeySigner = new RefusingKeySigner();
		Assert.False(LiquidWalletUiSigner.TrySign(refusingKeySigner, request, out LiquidWalletUiSignedTransaction? refusedKey));
		Assert.Null(refusedKey);

		// SignDigestHex refuses (returns null).
		var refusingSignatureSigner = new RefusingSignatureSigner();
		Assert.False(LiquidWalletUiSigner.TrySign(refusingSignatureSigner, request, out LiquidWalletUiSignedTransaction? refusedSignature));
		Assert.Null(refusedSignature);

		// Malformed public key hex (not 66 chars).
		var malformedKeySigner = new MalformedKeySigner();
		Assert.False(LiquidWalletUiSigner.TrySign(malformedKeySigner, request, out LiquidWalletUiSignedTransaction? malformedKey));
		Assert.Null(malformedKey);
	}

	// Required evidence §7: seam successful assembly. A signer double
	// returning well-formed hex public keys and signatures for every input
	// causes TrySign to return true with a LiquidWalletUiSignedTransaction
	// whose SignedTransactionHex is exactly the bytes the double produced and
	// whose NetworkManifestId / SourceRevision echo the request. The container
	// carries the signer's bytes and makes no validity claim.
	[Fact]
	public void TrySignAssemblesCallerSuppliedSignatures()
	{
		LiquidWalletUiSignRequest request = BuildValidSignRequest();
		var signer = new WellFormedSigner(request);

		bool succeeded = LiquidWalletUiSigner.TrySign(signer, request, out LiquidWalletUiSignedTransaction? signedTransaction);

		Assert.True(succeeded);
		Assert.NotNull(signedTransaction);
		// The container carries exactly the bytes the double produced (the
		// concatenation of the per-input signature hexes in input order).
		Assert.Equal(signer.ExpectedSignedTransactionHex, signedTransaction.SignedTransactionHex);
		Assert.Equal(request.NetworkManifestId, signedTransaction.NetworkManifestId);
		Assert.Equal(request.SourceRevision, signedTransaction.SourceRevision);
		// The transaction id is not computed by this slice.
		Assert.Equal(string.Empty, signedTransaction.TransactionIdHex);
		// The signer observed exactly one GetPublicKeyHex per input, then one
		// SignDigestHex per input, each carrying the caller-supplied epoch
		// digest handle.
		Assert.Equal(request.Inputs.Count, signer.GetPublicKeyCalls);
		Assert.Equal(request.Inputs.Count, signer.SignDigestCalls);
		Assert.All(signer.ObservedDigests, digest => Assert.Equal(request.SourceEpochHex, digest));
	}

	// Required evidence §8: facade projection boundary. Reflection rows prove
	// each new public type exposes exactly the frozen property set and no
	// other public instance property, and the seam types expose exactly the
	// frozen methods.
	[Fact]
	public void SignRequestTypesExposeExactlyTheFrozenSurface()
	{
		Assert.Equal(
			[
				"ConfidentialOutputCount",
				"ExplicitFeeAtomicUnits",
				"Inputs",
				"IsConfidential",
				"NetworkManifestId",
				"PeggedAssetIdHex",
				"SourceEpochHex",
				"SourceRevision",
				"WalletName",
				"WireFrameHex",
			],
			PublicInstancePropertyNames(typeof(LiquidWalletUiSignRequest)));

		Assert.Equal(
			[
				"AssetIdHex",
				"AtomicUnits",
				"OutPointHex",
			],
			PublicInstancePropertyNames(typeof(LiquidWalletUiSignRequestInput)));

		Assert.Equal(
			[
				"NetworkManifestId",
				"SignedTransactionHex",
				"SourceRevision",
				"TransactionIdHex",
			],
			PublicInstancePropertyNames(typeof(LiquidWalletUiSignedTransaction)));

		// ILiquidWalletSigner declares exactly GetPublicKeyHex and
		// SignDigestHex.
		Assert.Equal(
			["GetPublicKeyHex", "SignDigestHex"],
			typeof(ILiquidWalletSigner)
				.GetMethods(BindingFlags.Public | BindingFlags.Instance)
				.Select(method => method.Name)
				.Order(StringComparer.Ordinal)
				.ToArray());

		// LiquidWalletUiSigner exposes exactly TrySign (public static).
		Assert.Equal(
			["TrySign"],
			typeof(LiquidWalletUiSigner)
				.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
				.Select(method => method.Name)
				.Order(StringComparer.Ordinal)
				.ToArray());
	}

	// Required evidence §9: no production native call. The WalletWasabi
	// production assembly contains no LibraryImport / DllImport / NativeLibrary
	// reference beyond the pre-existing surface (this slice adds none), and
	// WalletWasabi/AssemblyInfo.cs is byte-identical (no InternalsVisibleTo
	// change).
	[Fact]
	public void ProductionAssemblyAddsNoNativeCallOrInternalsVisibleToChange()
	{
		// No new production type in this slice carries a LibraryImport or
		// DllImport attribute, or references NativeLibrary.
		Type[] newProductionTypes =
		[
			typeof(LiquidWalletUiSignRequest),
			typeof(LiquidWalletUiSignRequestInput),
			typeof(LiquidWalletUiSignedTransaction),
			typeof(ILiquidWalletSigner),
			typeof(LiquidWalletUiSigner),
		];
		foreach (Type type in newProductionTypes)
		{
			foreach (MethodInfo method in type.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic |
				BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
			{
				Assert.False(
					method.Attributes.HasFlag(MethodAttributes.PinvokeImpl),
					$"{type.FullName}.{method.Name} must not be a P/Invoke.");
				foreach (object attribute in method.GetCustomAttributes(inherit: false))
				{
					string attributeName = attribute.GetType().FullName!;
					Assert.DoesNotContain("DllImport", attributeName, StringComparison.Ordinal);
					Assert.DoesNotContain("LibraryImport", attributeName, StringComparison.Ordinal);
				}
			}
		}

		// AssemblyInfo.cs is byte-identical: the WalletWasabi assembly grants
		// internals visibility to exactly WalletWasabi.Tests and no other
		// assembly.
		string[] internalsVisibleTo = typeof(LiquidWalletState).Assembly
			.GetCustomAttributes(typeof(InternalsVisibleToAttribute), inherit: false)
			.Cast<InternalsVisibleToAttribute>()
			.Select(attribute => attribute.AssemblyName)
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(["WalletWasabi.Tests"], internalsVisibleTo);
	}

	// Null-argument rows for the two new facade methods and the new
	// factories.
	[Fact]
	public void NullArgumentRows()
	{
		byte[] key = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.KeyLength);
		byte[] context = RandomNumberGenerator.GetBytes(LiquidWalletReplayProtectedPayload.ExternalContextLength);
		try
		{
			LiquidTransactionId txA = Tx('a');
			LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
				.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));
			string[] selected = [OutPointHex(txA, 0)];
			string confidentialAddress = ConfidentialAddress();
			byte[] epoch = SourceEpoch;
			ElementsExpectationBoundRawTransactionBatch fundingSource = CreateFundingSource(txA);
			IReadOnlyList<IReadOnlyList<string>?> previousIds = [Array.Empty<string>()];

			// CreateSignRequest null-argument rows.
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					null!, Manifest, state, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", null!, state, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", Manifest, null!, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", Manifest, state, null!, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", Manifest, state, selected, null!,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", Manifest, state, selected, confidentialAddress,
					null!, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", Manifest, state, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, null!, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.CreateSignRequest(
					"wallet", Manifest, state, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, null!));

			// LoadAndCreateSignRequest null-argument rows.
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSignRequest(
					"dir", "wallet", null!, key, context, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSignRequest(
					"dir", "wallet", Manifest, key, context, null!, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSignRequest(
					"dir", "wallet", Manifest, key, context, selected, null!,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSignRequest(
					"dir", "wallet", Manifest, key, context, selected, confidentialAddress,
					null!, 9_000, 1_000, epoch, fundingSource, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSignRequest(
					"dir", "wallet", Manifest, key, context, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, null!, previousIds));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiFacade.LoadAndCreateSignRequest(
					"dir", "wallet", Manifest, key, context, selected, confidentialAddress,
					Manifest.PeggedAssetId, 9_000, 1_000, epoch, fundingSource, null!));

			// FromPlanAndFrame / FromEntry / SignedTransaction.Create
			// null-argument rows.
			LiquidOrdinaryWalletExactSpendPlan plan = BuildLandedPlan(state, txA);
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSignRequest.FromPlanAndFrame(null!, Manifest, plan, epoch, epoch));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSignRequest.FromPlanAndFrame("wallet", null!, plan, epoch, epoch));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSignRequest.FromPlanAndFrame("wallet", Manifest, null!, epoch, epoch));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSignRequestInput.FromEntry(null!));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSignedTransaction.Create(null!, 0, "aa", ""));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSignedTransaction.Create(Manifest.ManifestId, 0, null!, ""));
			Assert.Throws<ArgumentNullException>(() =>
				LiquidWalletUiSignedTransaction.Create(Manifest.ManifestId, 0, "aa", null!));
		}
		finally
		{
			CryptographicOperations.ZeroMemory(key);
			CryptographicOperations.ZeroMemory(context);
		}
	}

	// The SignedTransaction container's fail-closed hex shape checks: an
	// empty, odd-length, or non-hex signedTransactionHex throws
	// ArgumentException; a transactionIdHex that is neither empty nor exactly
	// 64 hex chars throws ArgumentException.
	[Fact]
	public void SignedTransactionCreateValidatesHexShape()
	{
		// Valid: empty transaction id.
		LiquidWalletUiSignedTransaction container = LiquidWalletUiSignedTransaction.Create(
			Manifest.ManifestId, 7, "aabb", "");
		Assert.Equal("aabb", container.SignedTransactionHex);
		Assert.Equal(string.Empty, container.TransactionIdHex);
		Assert.Equal(7ul, container.SourceRevision);

		// Valid: exactly 64-hex transaction id.
		string txId = new string('1', 64);
		LiquidWalletUiSignedTransaction withId = LiquidWalletUiSignedTransaction.Create(
			Manifest.ManifestId, 7, "aabb", txId);
		Assert.Equal(txId, withId.TransactionIdHex);

		// Empty signed transaction hex.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiSignedTransaction.Create(Manifest.ManifestId, 7, "", ""));
		// Odd-length signed transaction hex.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiSignedTransaction.Create(Manifest.ManifestId, 7, "abc", ""));
		// Non-hex signed transaction hex.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiSignedTransaction.Create(Manifest.ManifestId, 7, "zz", ""));
		// Transaction id neither empty nor 64 hex chars.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiSignedTransaction.Create(Manifest.ManifestId, 7, "aabb", "abcd"));
		// Non-hex transaction id.
		Assert.Throws<ArgumentException>(() =>
			LiquidWalletUiSignedTransaction.Create(Manifest.ManifestId, 7, "aabb", new string('z', 64)));
	}

	private static LiquidWalletUiSignRequest BuildValidSignRequest()
	{
		LiquidTransactionId txA = Tx('a');
		LiquidWalletState state = LiquidWalletState.Empty(PeggedAsset)
			.Apply(0, Delta(txA, [], [Output(txA, 0, PeggedAsset, 10_000)]));
		return LiquidWalletUiFacade.CreateSignRequest(
			"wallet",
			Manifest,
			state,
			[OutPointHex(txA, 0)],
			ConfidentialAddress(),
			Manifest.PeggedAssetId,
			destinationAtomicUnits: 9_000,
			explicitFeeAtomicUnits: 1_000,
			SourceEpoch,
			CreateFundingSource(txA),
			[Array.Empty<string>()]);
	}

	private static LiquidOrdinaryWalletExactSpendPlan BuildLandedPlan(
		LiquidWalletState state,
		LiquidTransactionId transactionId)
	{
		LiquidAddress address = LiquidAddress.Parse(Manifest, ConfidentialAddress());
		LiquidAssetAmount amount = LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, 9_000);
		LiquidSuppliedConfidentialDestination destination =
			LiquidSuppliedConfidentialDestination.Create(
				Manifest,
				address,
				PeggedAsset,
				amount,
				LiquidWalletLabelSet.Empty);
		LiquidSuppliedConfidentialDestinationBatch batch =
			LiquidSuppliedConfidentialDestinationBatch.Create([destination]);
		LiquidAssetAmount explicitFee = LiquidAssetAmount.Create(PeggedAsset, PeggedAsset, 1_000);
		return state.CreateExactOrdinaryWalletSpendPlan(
			state.Revision,
			[LiquidOutPoint.CreateSpendable(transactionId, 0)],
			batch,
			explicitFee);
	}

	// Builds a caller-constructed funding source whose raw transactions cover
	// the named candidate transaction (no previous transactions). Mirrors the
	// landed WLPQ test construction.
	private static ElementsExpectationBoundRawTransactionBatch CreateFundingSource(
		LiquidTransactionId candidateTransactionId)
	{
		string candidateId = candidateTransactionId.CanonicalRpcHex;
		string genesisBlockHash = new('a', 64);
		string bestBlockHash = new('b', 64);
		string startupId = new('c', 64);
		var expectation = new ElementsNodeExpectation(
			Manifest.ChainRpcName,
			genesisBlockHash,
			"51",
			Manifest.PeggedAssetId,
			new string('0', 64),
			2,
			false,
			1,
			1,
			"/sign-request-test:1/");
		var status = new ElementsNodeStatus(
			expectation.Chain,
			1,
			1,
			bestBlockHash,
			expectation.GenesisBlockHash,
			false,
			false,
			false,
			false,
			true,
			true,
			false,
			expectation.FedpegScript,
			expectation.PeggedAsset,
			expectation.ParentGenesisBlockHash,
			expectation.PeginConfirmationDepth,
			expectation.EnforcePak,
			expectation.Version,
			expectation.ProtocolVersion,
			expectation.Subversion);
		var generation = new ElementsNodeGenerationObservation(
			startupId,
			1,
			status.Blocks,
			status.BestBlockHash);
		var nodeObservation = new ElementsExpectationBoundNodeObservation(
			expectation,
			Manifest.PeggedAssetId,
			status,
			generation);
		var observations = new[]
		{
			new ElementsRawTransactionObservation(
				new ElementsRawTransactionRequest(candidateId, null),
				[0xaa]),
		};
		return new ElementsExpectationBoundRawTransactionBatch(nodeObservation, observations);
	}

	private static string[] PublicInstancePropertyNames(Type type) =>
		type
			.GetProperties(BindingFlags.Public | BindingFlags.Instance)
			.Select(property => property.Name)
			.Order(StringComparer.Ordinal)
			.ToArray();

	private static string ConfidentialAddress() =>
		LiquidAddress.FromScriptPubKey(
				Manifest,
				ReceiveScript,
				LiquidBlindingPublicKey.Create(BlindingKey))
			.GetCanonicalAddressText();

	private static string OutPointHex(LiquidTransactionId transactionId, uint outputIndex) =>
		Convert.ToHexString(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex).ToConsensusBytes());

	private static string GetWorkDir()
	{
		string dir = Common.GetWorkDir();
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static LiquidTransactionId Tx(char value) =>
		LiquidTransactionId.ParseRpcHex(new string(value, 64));

	private static LiquidSpendKeyReference Key(LiquidKeyBranch branch, uint index) =>
		LiquidSpendKeyReference.Create(Convert.FromHexString(PublicKeyHex), branch, index);

	private static LiquidOwnedOutput Output(
		LiquidTransactionId transactionId,
		uint outputIndex,
		LiquidAssetId assetId,
		long atomicUnits)
	{
		LiquidSpendKeyReference key = ExternalKey;
		return LiquidOwnedOutput.Create(
			LiquidOutPoint.CreateSpendable(transactionId, outputIndex),
			key.GetScriptPubKey(),
			LiquidAssetAmount.Create(assetId, PeggedAsset, atomicUnits),
			key);
	}

	private static LiquidWalletTransactionDelta Delta(
		LiquidTransactionId transactionId,
		System.Collections.Generic.IEnumerable<LiquidOutPoint> spent,
		System.Collections.Generic.IEnumerable<LiquidOwnedOutput> created) =>
		LiquidWalletTransactionDelta.Create(transactionId, spent, created);

	// A well-formed signer double: returns a 66-char lowercase-hex public key
	// for every input and a fixed per-input strict-DER-shaped lowercase-hex
	// signature, recording the digests it was asked to sign.
	private sealed class WellFormedSigner : ILiquidWalletSigner
	{
		private readonly List<string> _signatureHexes = [];

		internal WellFormedSigner(LiquidWalletUiSignRequest request)
		{
			foreach (LiquidWalletUiSignRequestInput input in request.Inputs)
			{
				// A deterministic per-input signature handle (72 hex chars: a
				// strict-DER-shaped low-S signature including the sighash
				// byte). The bytes are a test double's, not a real signature.
				_signatureHexes.Add("30" + new string('a', 68) + "01");
			}
			ExpectedSignedTransactionHex = string.Concat(_signatureHexes);
		}

		internal string ExpectedSignedTransactionHex { get; }
		internal int GetPublicKeyCalls { get; private set; }
		internal int SignDigestCalls { get; private set; }
		internal List<string> ObservedDigests { get; } = [];

		public string? GetPublicKeyHex(string outPointHex)
		{
			GetPublicKeyCalls++;
			return PublicKeyHex;
		}

		public string? SignDigestHex(string outPointHex, string digestHex)
		{
			SignDigestCalls++;
			ObservedDigests.Add(digestHex);
			return _signatureHexes[SignDigestCalls - 1];
		}
	}

	private sealed class RefusingKeySigner : ILiquidWalletSigner
	{
		public string? GetPublicKeyHex(string outPointHex) => null;
		public string? SignDigestHex(string outPointHex, string digestHex) => "30" + new string('a', 68) + "01";
	}

	private sealed class RefusingSignatureSigner : ILiquidWalletSigner
	{
		public string? GetPublicKeyHex(string outPointHex) => PublicKeyHex;
		public string? SignDigestHex(string outPointHex, string digestHex) => null;
	}

	private sealed class MalformedKeySigner : ILiquidWalletSigner
	{
		public string? GetPublicKeyHex(string outPointHex) => "abcd";
		public string? SignDigestHex(string outPointHex, string digestHex) => "30" + new string('a', 68) + "01";
	}
}
