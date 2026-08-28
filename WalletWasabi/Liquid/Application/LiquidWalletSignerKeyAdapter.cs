using System;
using System.Security.Cryptography;
using System.Threading;
using NBitcoin;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Liquid.Application;

/// <summary>
/// Managed production implementation of the caller-owned <see cref="ILiquidWalletSigner"/>
/// boundary: derives per-input spend keys from an extended master key and signs the
/// caller-computed digests, without ever returning a secret key across the seam. Spend keys
/// live at <c>m/2089617494h/1984574463h/accountH/change/index</c>; the translation from an
/// outpoint to its BIP32 coordinates is delegated to the caller-supplied locator, so this
/// adapter never parses transaction data. Every method is fail-closed: an unknown outpoint,
/// a malformed digest, or a disposed adapter yields <see langword="null"/>, never a partial
/// or substituted result. <see cref="Dispose"/> zeroizes the adapter's retained copy of the
/// master secret and disposes the underlying key idempotently.
/// </summary>
internal sealed class LiquidWalletSignerKeyAdapter : ILiquidWalletSigner, IDisposable
{
	private const uint PurposeBranch = 2089617494;
	private const uint CoinTypeBranch = 1984574463;

	private readonly ExtKey _masterKey;
	private readonly Func<string, (int Account, int Change, int Index)?> _outpointLocator;
	private readonly byte[] _masterKeyBytes;
	private int _disposed;

	/// <summary>
	/// Takes ownership of the extended master key whose spend branches sit under the hardened
	/// prefix <c>m/2089617494h/1984574463h</c>. The locator maps an outpoint (in the same hex
	/// form the signing seam hands it over) to its BIP32 coordinates; a locator that cannot
	/// place an outpoint signals refusal by throwing, which the adapter turns into the
	/// fail-closed <see langword="null"/> response. The network pins wallet composition
	/// context only — derivation and signing are network-agnostic — but it must be supplied.
	/// </summary>
	public LiquidWalletSignerKeyAdapter(
		ExtKey masterKey,
		Func<string, (int account, int change, int index)?> outpointLocator,
		NBitcoin.Network network)
	{
		ArgumentNullException.ThrowIfNull(masterKey);
		ArgumentNullException.ThrowIfNull(outpointLocator);
		ArgumentNullException.ThrowIfNull(network);

		_masterKey = masterKey;
		_outpointLocator = outpointLocator;
		_masterKeyBytes = masterKey.PrivateKey.ToBytes();
	}

	/// <inheritdoc />
	public string? GetPublicKeyHex(string outPointHex)
	{
		if (Volatile.Read(ref _disposed) != 0 || outPointHex is null)
		{
			return null;
		}

		// The master key is a compressed-key ExtKey, so every derived public key is
		// already the 33-byte compressed form; ToHex emits its 66-character form.
		Key? spendKey = TryDeriveSpendKey(outPointHex);
		return spendKey?.PubKey.ToHex();
	}

	/// <inheritdoc />
	public string? SignDigestHex(string outPointHex, string digestHex)
	{
		if (Volatile.Read(ref _disposed) != 0 || outPointHex is null || digestHex is null)
		{
			return null;
		}

		byte[] digest;
		try
		{
			digest = Convert.FromHexString(digestHex);
		}
		catch (Exception exception) when (exception is FormatException or ArgumentException)
		{
			return null;
		}

		if (digest.Length != 32)
		{
			CryptographicOperations.ZeroMemory(digest);
			return null;
		}

		Key? spendKey = TryDeriveSpendKey(outPointHex);
		if (spendKey is null)
		{
			CryptographicOperations.ZeroMemory(digest);
			return null;
		}

		try
		{
			// The digest bytes are the big-endian 32-byte sighash; uint256 must not
			// reinterpret them in little-endian order.
			uint256 hash = new(digest, lendian: false);

			// The native binding validates strict DER, so the signature crosses the
			// seam as the canonical low-S DER encoding rather than a compact form.
			return Convert.ToHexStringLower(spendKey.Sign(hash).ToDER());
		}
		finally
		{
			CryptographicOperations.ZeroMemory(digest);
		}
	}

	/// <summary>
	/// Zeroizes the adapter's retained copy of the master secret bytes and disposes the
	/// underlying key. Idempotent: later calls are no-ops, and every seam method refuses
	/// with <see langword="null"/> afterwards.
	/// </summary>
	public void Dispose()
	{
		if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
		{
			return;
		}

		CryptographicOperations.ZeroMemory(_masterKeyBytes);
		_masterKey.PrivateKey.Dispose();
	}

	private Key? TryDeriveSpendKey(string outPointHex)
	{
		try
		{
			(int account, int change, int index)? coordinates = _outpointLocator(outPointHex);
			if (coordinates is not { } location
				|| location.account < 0
				|| (uint)location.change > 1u
				|| location.index < 0)
			{
				return null;
			}

			return _masterKey
				.Derive(new KeyPath($"{PurposeBranch}h/{CoinTypeBranch}h/{location.account}h/{location.change}/{location.index}"))
				.PrivateKey;
		}
		catch (Exception)
		{
			// Any locator or derivation failure is a fail-closed refusal, never a fallback.
			return null;
		}
	}
}
