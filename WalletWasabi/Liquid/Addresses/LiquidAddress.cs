using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using ValidatedLiquidAddress = (string NetworkManifestId, WalletWasabi.Liquid.Addresses.LiquidAddressKind Kind, byte? WitnessVersion, string CanonicalAddressText, string UnconfidentialAddressText, byte[] ScriptPubKey, WalletWasabi.Liquid.Cryptography.LiquidBlindingPublicKey? BlindingPublicKey);

namespace WalletWasabi.Liquid.Addresses;

internal enum LiquidAddressKind
{
	PayToPubKeyHash,
	PayToScriptHash,
	WitnessV0KeyHash,
	WitnessV0ScriptHash,
	WitnessV1Taproot,
	WitnessUnknown,
}

internal enum LiquidAddressParseFailure
{
	InvalidEncoding,
	NetworkMismatch,
}

internal sealed class LiquidAddressFormatException : FormatException
{
	internal LiquidAddressFormatException(LiquidAddressParseFailure failure)
		: base("The Liquid address could not be accepted.")
	{
		Failure = failure;
	}

	public LiquidAddressParseFailure Failure { get; }
}

internal sealed class LiquidAddress : IEquatable<LiquidAddress>
{
	private readonly string _canonicalAddressText;
	private readonly string _unconfidentialAddressText;
	private readonly byte[] _scriptPubKey;
	private readonly LiquidBlindingPublicKey? _blindingPublicKey;

	private LiquidAddress(ValidatedLiquidAddress validated)
	{
		NetworkManifestId = validated.NetworkManifestId;
		Kind = validated.Kind;
		WitnessVersion = validated.WitnessVersion;
		_canonicalAddressText = validated.CanonicalAddressText;
		_unconfidentialAddressText = validated.UnconfidentialAddressText;
		_scriptPubKey = [.. validated.ScriptPubKey];
		_blindingPublicKey = validated.BlindingPublicKey;
	}

	public string NetworkManifestId { get; }
	public LiquidAddressKind Kind { get; }
	public bool IsConfidential => _blindingPublicKey is not null;
	public byte? WitnessVersion { get; }

	public static LiquidAddress Parse(
		ElementsPublicNetworkManifest manifest,
		string encodedAddress) =>
		new(LiquidAddressCodec.Parse(manifest, encodedAddress));

	public static LiquidAddress FromScriptPubKey(
		ElementsPublicNetworkManifest manifest,
		ReadOnlySpan<byte> scriptPubKey,
		LiquidBlindingPublicKey? blindingPublicKey = null) =>
		new(LiquidAddressCodec.FromScriptPubKey(manifest, scriptPubKey, blindingPublicKey));

	public string GetCanonicalAddressText() => _canonicalAddressText;

	public string GetUnconfidentialAddressText() => _unconfidentialAddressText;

	public byte[] GetScriptPubKey() => [.. _scriptPubKey];

	public byte[]? GetBlindingPublicKey() => _blindingPublicKey?.GetCompressedPublicKey();

	public bool Equals(LiquidAddress? other) =>
		other is not null &&
		StringComparer.Ordinal.Equals(NetworkManifestId, other.NetworkManifestId) &&
		_scriptPubKey.AsSpan().SequenceEqual(other._scriptPubKey) &&
		Equals(_blindingPublicKey, other._blindingPublicKey);

	public override bool Equals(object? obj) => Equals(obj as LiquidAddress);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(NetworkManifestId, StringComparer.Ordinal);
		foreach (byte value in _scriptPubKey)
		{
			hash.Add(value);
		}
		hash.Add(_blindingPublicKey);
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidAddress);
}
