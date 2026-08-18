namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The Liquid signer-seam driver: drives a caller-supplied
/// <see cref="ILiquidWalletSigner"/> over one <see cref="LiquidWalletUiSignRequest"/>
/// and assembles the returned signatures into a
/// <see cref="LiquidWalletUiSignedTransaction"/> container. This slice
/// performs no signing, no sighash computation, no signature verification,
/// and no native call; it assembles caller-supplied signatures against
/// caller-supplied digests only. The per-input digest handle passed to
/// <see cref="ILiquidWalletSigner.SignDigestHex"/> is the request's
/// caller-supplied <see cref="LiquidWalletUiSignRequest.SourceEpochHex"/> —
/// the driver does not compute a consensus sighash (that requires the native
/// sighash-with-rangeproof computation, bound only when a later
/// separately-reviewed production FFI slice binds the native signing
/// surface). A <see langword="null"/> signer, a <see langword="null"/>
/// request, a refusing or malformed signer return, yields
/// <see langword="false"/> and a <see langword="null"/> signed transaction —
/// fail-closed, no partial result, no retry, no fallback.
/// </summary>
public static class LiquidWalletUiSigner
{
	public static bool TrySign(
		ILiquidWalletSigner signer,
		LiquidWalletUiSignRequest request,
		out LiquidWalletUiSignedTransaction? signedTransaction)
	{
		signedTransaction = null;
		if (signer is null || request is null)
		{
			return false;
		}

		IReadOnlyList<LiquidWalletUiSignRequestInput> inputs = request.Inputs;
		if (inputs is null)
		{
			return false;
		}

		// Collect every public key first (one call per input, in the landed
		// plan order) before any digest is requested. A null or malformed
		// public key is a fail-closed refusal.
		var publicKeyHexes = new string[inputs.Count];
		for (int index = 0; index < inputs.Count; index++)
		{
			LiquidWalletUiSignRequestInput input = inputs[index];
			if (input is null)
			{
				return false;
			}
			string? publicKeyHex = signer.GetPublicKeyHex(input.OutPointHex);
			if (!IsHexOfLength(publicKeyHex, 66))
			{
				return false;
			}
			publicKeyHexes[index] = publicKeyHex!;
		}

		// The caller-supplied digest handle: the request's source epoch. The
		// driver computes nothing; it forwards the caller-bound handle.
		string digestHex = request.SourceEpochHex;
		var signatureHexes = new string[inputs.Count];
		for (int index = 0; index < inputs.Count; index++)
		{
			string? signatureHex = signer.SignDigestHex(inputs[index].OutPointHex, digestHex);
			if (!IsHex(signatureHex) || signatureHex!.Length == 0)
			{
				return false;
			}
			signatureHexes[index] = signatureHex;
		}

		// Assemble the caller-returned bytes into the container. The container
		// asserts production, not validity; the signed-transaction hex is the
		// concatenation of the caller-returned signature bytes in input order.
		// The transaction id is not computed by this slice (the empty string).
		string signedTransactionHex = string.Concat(signatureHexes);
		try
		{
			signedTransaction = LiquidWalletUiSignedTransaction.Create(
				request.NetworkManifestId,
				request.SourceRevision,
				signedTransactionHex,
				string.Empty);
			return true;
		}
		catch (ArgumentException)
		{
			signedTransaction = null;
			return false;
		}
	}

	private static bool IsHexOfLength(string? value, int length) =>
		value is not null && value.Length == length && IsHex(value);

	private static bool IsHex(string? value)
	{
		if (value is null || value.Length % 2 != 0)
		{
			return false;
		}
		foreach (char character in value)
		{
			if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
			{
				return false;
			}
		}
		return true;
	}
}
