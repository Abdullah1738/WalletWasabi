using System.Collections.ObjectModel;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;

namespace WalletWasabi.Liquid.Wallet;

internal sealed class LiquidSuppliedConfidentialDestinationBatch :
	IEquatable<LiquidSuppliedConfidentialDestinationBatch>
{
	public const int MaximumDestinationCount = 256;

	private readonly string _networkManifestId;
	private readonly LiquidAssetId _peggedAssetId;
	private readonly LiquidSuppliedConfidentialDestination[] _destinations;
	private readonly LiquidAssetBalanceMap _requestedAmounts;

	private LiquidSuppliedConfidentialDestinationBatch(
		string networkManifestId,
		LiquidAssetId peggedAssetId,
		LiquidSuppliedConfidentialDestination[] destinations,
		LiquidAssetBalanceMap requestedAmounts)
	{
		_networkManifestId = networkManifestId;
		_peggedAssetId = peggedAssetId;
		_destinations = destinations;
		_requestedAmounts = requestedAmounts;
	}

	public int Count => _destinations.Length;

	public static LiquidSuppliedConfidentialDestinationBatch Create(
		IReadOnlyList<LiquidSuppliedConfidentialDestination> destinations)
	{
		ArgumentNullException.ThrowIfNull(destinations);

		int count = destinations.Count;
		if (count is < 1 or > MaximumDestinationCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(destinations),
				"The supplied Liquid destination batch could not be accepted.");
		}

		var snapshot = new LiquidSuppliedConfidentialDestination[count];
		for (int index = 0; index < snapshot.Length; index++)
		{
			snapshot[index] = destinations[index];
		}

		for (int index = 0; index < snapshot.Length; index++)
		{
			if (snapshot[index] is null)
			{
				throw new ArgumentException(
					"The supplied Liquid destination batch could not be accepted.",
					nameof(destinations));
			}
		}

		var capturedAmounts = new LiquidAssetAmount[snapshot.Length];
		for (int index = 0; index < snapshot.Length; index++)
		{
			capturedAmounts[index] = snapshot[index].GetAmount() ??
				throw new ArgumentException(
					"The supplied Liquid destination batch could not be accepted.",
					nameof(destinations));
		}

		string networkManifestId = snapshot[0].GetNetworkManifestId();
		LiquidAssetId peggedAssetId = snapshot[0].GetPeggedAssetId();
		for (int index = 1; index < snapshot.Length; index++)
		{
			if (!StringComparer.Ordinal.Equals(
				networkManifestId,
				snapshot[index].GetNetworkManifestId()))
			{
				throw new ArgumentException(
					"The supplied Liquid destination batch could not be accepted.",
					nameof(destinations));
			}

			if (peggedAssetId != snapshot[index].GetPeggedAssetId())
			{
				throw new ArgumentException(
					"The supplied Liquid destination batch could not be accepted.",
					nameof(destinations));
			}
		}

		string ownedNetworkManifestId = new(networkManifestId.AsSpan());
		LiquidAssetId ownedPeggedAssetId = CopyAssetId(peggedAssetId);
		LiquidAssetId mapPeggedAssetId = CopyAssetId(peggedAssetId);
		var ownedAmounts = new LiquidAssetAmount[capturedAmounts.Length];
		for (int index = 0; index < capturedAmounts.Length; index++)
		{
			ownedAmounts[index] = CopyAmount(capturedAmounts[index]);
		}

		LiquidAssetBalanceMap requestedAmounts =
			LiquidAssetBalanceMap.FromAmounts(mapPeggedAssetId, ownedAmounts);
		return new LiquidSuppliedConfidentialDestinationBatch(
			ownedNetworkManifestId,
			ownedPeggedAssetId,
			snapshot,
			requestedAmounts);
	}

	public string GetNetworkManifestId() => _networkManifestId;

	public LiquidAssetId GetPeggedAssetId() => _peggedAssetId;

	public IReadOnlyList<LiquidSuppliedConfidentialDestination> GetDestinations() =>
		new ReadOnlyCollection<LiquidSuppliedConfidentialDestination>(
			(LiquidSuppliedConfidentialDestination[])_destinations.Clone());

	public LiquidAssetBalanceMap GetRequestedAmounts()
	{
		LiquidAssetId peggedAssetId = CopyAssetId(_requestedAmounts.PeggedAssetId);
		IReadOnlyList<LiquidAssetAmount> amounts = _requestedAmounts.GetAmounts();
		var copiedAmounts = new LiquidAssetAmount[amounts.Count];
		for (int index = 0; index < copiedAmounts.Length; index++)
		{
			copiedAmounts[index] = CopyAmount(amounts[index]);
		}

		return LiquidAssetBalanceMap.FromAmounts(peggedAssetId, copiedAmounts);
	}

	public bool Equals(LiquidSuppliedConfidentialDestinationBatch? other)
	{
		if (ReferenceEquals(this, other))
		{
			return true;
		}
		if (other is null ||
			!StringComparer.Ordinal.Equals(_networkManifestId, other._networkManifestId) ||
			_peggedAssetId != other._peggedAssetId ||
			_destinations.Length != other._destinations.Length)
		{
			return false;
		}

		for (int index = 0; index < _destinations.Length; index++)
		{
			if (!_destinations[index].Equals(other._destinations[index]))
			{
				return false;
			}
		}

		return true;
	}

	public override bool Equals(object? obj) =>
		Equals(obj as LiquidSuppliedConfidentialDestinationBatch);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(_networkManifestId, StringComparer.Ordinal);
		hash.Add(_peggedAssetId);
		foreach (LiquidSuppliedConfidentialDestination destination in _destinations)
		{
			hash.Add(destination);
		}
		return hash.ToHashCode();
	}

	public override string ToString() => nameof(LiquidSuppliedConfidentialDestinationBatch);

	private static LiquidAssetAmount CopyAmount(LiquidAssetAmount amount) =>
		LiquidAssetAmount.Create(
			CopyAssetId(amount.AssetId),
			CopyAssetId(amount.PeggedAssetId),
			amount.AtomicUnits);

	private static LiquidAssetId CopyAssetId(LiquidAssetId assetId) =>
		LiquidAssetId.ParseConsensusBytes(assetId.ToConsensusBytes());
}
