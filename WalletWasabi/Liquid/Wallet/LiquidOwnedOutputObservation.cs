using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet;

/// <summary>
/// Normalized public facts shaped like one independently validated owned
/// confidential-output observation. Constructing this value does not itself
/// prove transaction validity and carries no chain, unspentness, confirmation,
/// balance-credit, persistence, or signing authority.
/// </summary>
internal sealed class LiquidOwnedOutputObservation : IEquatable<LiquidOwnedOutputObservation>
{
	public const uint MaxDerivationIndex = LiquidSpendKeyReference.MaximumIndex;

	private readonly LiquidOutPoint _outPoint;
	private readonly LiquidTransactionWitnessBinding _transactionWitnessBinding;
	private readonly byte[] _scriptPubKey;
	private readonly LiquidSpendKeyReference _spendKey;
	private readonly LiquidBlindingPublicKey _blindingPublicKey;
	private readonly LiquidAssetId _assetId;

	private LiquidOwnedOutputObservation(
		LiquidOutPoint outPoint,
		LiquidTransactionWitnessBinding transactionWitnessBinding,
		byte[] scriptPubKey,
		LiquidSpendKeyReference spendKey,
		LiquidBlindingPublicKey blindingPublicKey,
		LiquidAssetId assetId,
		long value)
	{
		_outPoint = outPoint;
		_transactionWitnessBinding = transactionWitnessBinding;
		_scriptPubKey = scriptPubKey;
		_spendKey = spendKey;
		_blindingPublicKey = blindingPublicKey;
		_assetId = assetId;
		Value = value;
	}

	public uint OutputIndex => _outPoint.OutputIndex;
	public LiquidKeyBranch Branch => _spendKey.Branch;
	public uint DerivationIndex => _spendKey.Index;
	public long Value { get; }

	public static LiquidOwnedOutputObservation Create(
		ReadOnlySpan<byte> transactionIdConsensusBytes,
		uint outputIndex,
		ReadOnlySpan<byte> transactionWitnessBinding,
		ReadOnlySpan<byte> scriptPubKey,
		ReadOnlySpan<byte> spendPublicKey,
		ReadOnlySpan<byte> blindingPublicKey,
		LiquidKeyBranch branch,
		uint derivationIndex,
		ReadOnlySpan<byte> assetIdConsensusBytes,
		ulong value)
	{
		if (derivationIndex > MaxDerivationIndex)
		{
			throw new ArgumentOutOfRangeException(
				nameof(derivationIndex),
				"A supported normal descriptor derivation index is required.");
		}
		if (value == 0 || value > long.MaxValue)
		{
			throw new ArgumentOutOfRangeException(
				nameof(value),
				"A positive signed 64-bit Liquid asset value is required.");
		}

		LiquidTransactionId transactionId = LiquidTransactionId.ParseConsensusBytes(
			transactionIdConsensusBytes,
			nameof(transactionIdConsensusBytes));
		LiquidOutPoint outPoint = LiquidOutPoint.CreateSpendable(transactionId, outputIndex);
		LiquidTransactionWitnessBinding witnessBinding =
			LiquidTransactionWitnessBinding.Create(transactionWitnessBinding);
		if (!Enum.IsDefined(branch))
		{
			throw new ArgumentOutOfRangeException(nameof(branch), "A supported Liquid key branch is required.");
		}

		LiquidSpendKeyReference spendKey;
		try
		{
			spendKey = LiquidSpendKeyReference.Create(spendPublicKey, branch, derivationIndex);
		}
		catch (ArgumentException)
		{
			throw new ArgumentException(
				"A valid compressed secp256k1 spend public key is required.",
				nameof(spendPublicKey));
		}
		if (!spendKey.MatchesScriptPubKey(scriptPubKey))
		{
			throw new ArgumentException(
				"A native P2WPKH script matching the observed spend public key is required.",
				nameof(scriptPubKey));
		}

		LiquidBlindingPublicKey validatedBlindingPublicKey =
			LiquidBlindingPublicKey.Create(blindingPublicKey);
		LiquidAssetId assetId = LiquidAssetId.ParseConsensusBytes(
			assetIdConsensusBytes,
			nameof(assetIdConsensusBytes));

		return new LiquidOwnedOutputObservation(
			outPoint,
			witnessBinding,
			scriptPubKey.ToArray(),
			spendKey,
			validatedBlindingPublicKey,
			assetId,
			checked((long)value));
	}

	public byte[] GetTransactionIdConsensusBytes() => _outPoint.TransactionId.ToConsensusBytes();

	public byte[] GetTransactionWitnessBinding() => _transactionWitnessBinding.GetBytes();

	public byte[] GetScriptPubKey() => [.. _scriptPubKey];

	public byte[] GetSpendPublicKey() => _spendKey.GetCompressedPublicKey();

	public byte[] GetBlindingPublicKey() => _blindingPublicKey.GetCompressedPublicKey();

	public byte[] GetAssetIdConsensusBytes() => _assetId.ToConsensusBytes();

	internal bool MatchesTransactionId(LiquidTransactionId transactionId) =>
		_outPoint.TransactionId == transactionId;

	internal bool MatchesTransactionWitnessBinding(LiquidTransactionWitnessBinding transactionWitnessBinding) =>
		_transactionWitnessBinding.Equals(transactionWitnessBinding);

	public bool Equals(LiquidOwnedOutputObservation? other) =>
		other is not null &&
		_outPoint == other._outPoint &&
		_transactionWitnessBinding.Equals(other._transactionWitnessBinding) &&
		_scriptPubKey.AsSpan().SequenceEqual(other._scriptPubKey) &&
		_spendKey.Equals(other._spendKey) &&
		_blindingPublicKey.Equals(other._blindingPublicKey) &&
		_assetId == other._assetId &&
		Value == other.Value;

	public override bool Equals(object? obj) => Equals(obj as LiquidOwnedOutputObservation);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(_outPoint);
		hash.Add(_transactionWitnessBinding);
		foreach (byte value in _scriptPubKey)
		{
			hash.Add(value);
		}
		hash.Add(_spendKey);
		hash.Add(_blindingPublicKey);
		hash.Add(_assetId);
		hash.Add(Value);
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidOwnedOutputObservation);
}
