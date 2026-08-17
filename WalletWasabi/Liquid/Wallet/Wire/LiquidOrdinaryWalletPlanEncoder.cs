using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Addresses;
using WalletWasabi.Liquid.Amounts;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Wire;

internal static partial class LiquidOrdinaryWalletPlanEncoder
{
	private static readonly object CooperationCapability = new();

	internal const string InvariantMessage =
		"Liquid ordinary-wallet plan wire type state is invalid.";

	/// <summary>
	/// Encodes one accepted exact plan and funding batch without acquiring external authority.
	/// The caller must supply a fresh unpredictable epoch for every wallet session it intends to
	/// isolate and must never reuse that epoch across sessions.
	/// The epoch is plaintext and not a secret, authenticator, authorization, MAC, or anti-replay token.
	/// Copying or reuse makes
	/// frames linkable. Validation is variable-time and can reveal an approximate failure phase to
	/// the in-process caller. Funding bytes remain declarations until native semantic preparation;
	/// this encoder does not bind actual confidential selected assets or values.
	/// </summary>
	internal static bool TryEncode(
		ReadOnlySpan<byte> sourceEpoch,
		LiquidOrdinaryWalletExactSpendPlan? plan,
		LiquidOrdinaryWalletPlanFundingBatch? fundingBatch,
		out LiquidOrdinaryWalletPlanEncodedFrame? frame,
		out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
	{
		frame = null;
		errorCode = LiquidOrdinaryWalletPlanWireErrorCode.None;
		if (plan is null || fundingBatch is null)
		{
			return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
		}

		return fundingBatch.TryEncode(
			CooperationCapability,
			sourceEpoch,
			plan,
			out frame,
			out errorCode);
	}

	private static bool TryEncodeLocked(
		ReadOnlySpan<byte> sourceEpoch,
		LiquidOrdinaryWalletExactSpendPlan plan,
		LiquidOrdinaryWalletExactSpendPlan? batchPlan,
		LiquidOrdinaryWalletPlanFundingRow[] rows,
		out LiquidOrdinaryWalletPlanEncodedFrame? frame,
		out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
	{
		frame = null;
		errorCode = LiquidOrdinaryWalletPlanWireErrorCode.None;
		if (sourceEpoch.Length != LiquidOrdinaryWalletPlanWireLimits.SourceEpochLength)
		{
			return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
		}

		Span<byte> sourceEpochScratch = stackalloc byte[LiquidOrdinaryWalletPlanWireLimits.SourceEpochLength];
		try
		{
			sourceEpoch.CopyTo(sourceEpochScratch);
			if (!IsNonzero(sourceEpochScratch))
			{
				return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
			}

			if (!ReferenceEquals(plan, batchPlan))
			{
				return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
			}

			frame = EncodeAcceptedPlan(sourceEpochScratch, plan, rows);
			errorCode = LiquidOrdinaryWalletPlanWireErrorCode.None;
			return true;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(sourceEpochScratch);
		}
	}

	private static LiquidOrdinaryWalletPlanEncodedFrame EncodeAcceptedPlan(
		ReadOnlySpan<byte> sourceEpoch,
		LiquidOrdinaryWalletExactSpendPlan plan,
		LiquidOrdinaryWalletPlanFundingRow[] rows)
	{
		ReadOnlySpan<LiquidWalletCoinControlEntry> selected = plan.GetSelectedEntriesForWireEncoding();
		ReadOnlySpan<LiquidSuppliedConfidentialDestination> destinations =
			plan.GetDestinationsForWireEncoding();
		ElementsPublicNetworkManifest context = ValidatePlan(plan, selected, destinations, out int addressBytes);
		if (rows.Length != selected.Length)
		{
			throw new InvalidOperationException(InvariantMessage);
		}

		int aggregatePreviousCount = 0;
		long aggregateTransactionBytes = 0;
		for (int index = 0; index < rows.Length; index++)
		{
			LiquidOrdinaryWalletPlanFundingRow row = rows[index] ??
				throw new InvalidOperationException(InvariantMessage);
			LiquidOrdinaryWalletPlanFundingRow.EncodingShape shape =
				row.GetEncodingShape(CooperationCapability);
			if (!TryCheckedAdd(aggregatePreviousCount, shape.PreviousCount, out aggregatePreviousCount) ||
				aggregatePreviousCount > LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount ||
				!TryCheckedAdd(
					aggregateTransactionBytes,
					shape.AggregateTransactionLength,
					out aggregateTransactionBytes) ||
				aggregateTransactionBytes > LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength)
			{
				throw new InvalidOperationException(InvariantMessage);
			}
		}

		long exactLength = LiquidOrdinaryWalletPlanWireLimits.HeaderLength;
		if (!TryCheckedAdd(
				exactLength,
				(long)selected.Length * LiquidOrdinaryWalletPlanWireLimits.SelectedFixedLength,
				out exactLength) ||
			!TryCheckedAdd(
				exactLength,
				(long)destinations.Length * LiquidOrdinaryWalletPlanWireLimits.DestinationFixedLength,
				out exactLength) ||
			!TryCheckedAdd(exactLength, addressBytes, out exactLength) ||
			!TryCheckedAdd(
				exactLength,
				(long)aggregatePreviousCount * LiquidOrdinaryWalletPlanWireLimits.PreviousLengthPrefix,
				out exactLength) ||
			!TryCheckedAdd(exactLength, aggregateTransactionBytes, out exactLength) ||
			exactLength > LiquidOrdinaryWalletPlanWireLimits.MaximumReachableFrameLength)
		{
			throw new InvalidOperationException(InvariantMessage);
		}

		byte[]? temporaryFrame = null;
		LiquidOrdinaryWalletPlanEncodedFrame? ownedFrame = null;
		try
		{
			temporaryFrame = new byte[checked((int)exactLength)];
			int cursor = 0;
			Write("WLPQ"u8, temporaryFrame, ref cursor);
			WriteUInt16(1, temporaryFrame, ref cursor);
			WriteUInt16(LiquidOrdinaryWalletPlanWireLimits.HeaderLength, temporaryFrame, ref cursor);
			WriteUInt64((ulong)exactLength, temporaryFrame, ref cursor);
			WriteUInt32(0, temporaryFrame, ref cursor);
			WriteUInt32(0, temporaryFrame, ref cursor);
			Write(sourceEpoch, temporaryFrame, ref cursor);
			WriteUInt64(plan.SourceRevision, temporaryFrame, ref cursor);
			WriteLowerHex(plan.GetDestinationNetworkManifestId(), temporaryFrame, ref cursor);
			plan.GetPeggedAssetId().WriteConsensusBytes(temporaryFrame.AsSpan(cursor, 32));
			cursor += 32;
			WriteUInt32((uint)selected.Length, temporaryFrame, ref cursor);
			WriteUInt32((uint)destinations.Length, temporaryFrame, ref cursor);
			WriteUInt32((uint)aggregatePreviousCount, temporaryFrame, ref cursor);
			WriteUInt32(0, temporaryFrame, ref cursor);
			WriteUInt64((ulong)plan.GetExplicitFee().AtomicUnits, temporaryFrame, ref cursor);
			for (int index = 0; index < selected.Length; index++)
			{
				LiquidWalletCoinControlEntry entry = selected[index];
				LiquidOrdinaryWalletPlanFundingRow.EncodingShape shape =
					rows[index].GetEncodingShape(CooperationCapability);
				entry.OutPoint.TransactionId.WriteConsensusBytes(temporaryFrame.AsSpan(cursor, 32));
				cursor += 32;
				WriteUInt32(entry.OutPoint.OutputIndex, temporaryFrame, ref cursor);
				entry.Amount.AssetId.WriteConsensusBytes(temporaryFrame.AsSpan(cursor, 32));
				cursor += 32;
				WriteUInt64((ulong)entry.Amount.AtomicUnits, temporaryFrame, ref cursor);
				WriteUInt32((uint)shape.CandidateLength, temporaryFrame, ref cursor);
				WriteUInt32((uint)shape.PreviousCount, temporaryFrame, ref cursor);
				WriteUInt32(0, temporaryFrame, ref cursor);
				rows[index].WritePayloads(CooperationCapability, temporaryFrame, ref cursor);
			}

			for (int index = 0; index < destinations.Length; index++)
			{
				LiquidSuppliedConfidentialDestination destination = destinations[index];
				string address = destination.GetAddress().GetCanonicalAddressText();
				destination.GetAssetId().WriteConsensusBytes(temporaryFrame.AsSpan(cursor, 32));
				cursor += 32;
				WriteUInt64((ulong)destination.GetAmount()!.AtomicUnits, temporaryFrame, ref cursor);
				WriteUInt32((uint)address.Length, temporaryFrame, ref cursor);
				WriteUInt32(0, temporaryFrame, ref cursor);
				int written = Encoding.ASCII.GetBytes(address, temporaryFrame.AsSpan(cursor, address.Length));
				if (written != address.Length)
				{
					throw new InvalidOperationException(InvariantMessage);
				}

				cursor += written;
			}

			if (cursor != exactLength ||
				!StringComparer.Ordinal.Equals(context.ManifestId, plan.GetDestinationNetworkManifestId()))
			{
				throw new InvalidOperationException(InvariantMessage);
			}

			ownedFrame = LiquidOrdinaryWalletPlanEncodedFrame.TakeOwnership(
				CooperationCapability,
				ref temporaryFrame);
			LiquidOrdinaryWalletPlanEncodedFrame result = ownedFrame;
			ownedFrame = null;
			return result;
		}
		finally
		{
			ownedFrame?.Dispose();
			if (temporaryFrame is not null)
			{
				CryptographicOperations.ZeroMemory(temporaryFrame);
			}
		}
	}

	private static ElementsPublicNetworkManifest ValidatePlan(
		LiquidOrdinaryWalletExactSpendPlan plan,
		ReadOnlySpan<LiquidWalletCoinControlEntry> selected,
		ReadOnlySpan<LiquidSuppliedConfidentialDestination> destinations,
		out int addressBytes)
	{
		if (selected.Length is < 1 or > LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount ||
			destinations.Length is < 1 or > LiquidOrdinaryWalletExactSpendPlan.MaximumConfidentialOutputCount)
		{
			throw new InvalidOperationException(InvariantMessage);
		}

		string manifestId = plan.GetDestinationNetworkManifestId();
		ElementsPublicNetworkManifest context =
			StringComparer.Ordinal.Equals(manifestId, ElementsPublicNetworkManifest.LiquidMainnet.ManifestId)
				? ElementsPublicNetworkManifest.LiquidMainnet
				: StringComparer.Ordinal.Equals(manifestId, ElementsPublicNetworkManifest.LiquidTestnet.ManifestId)
					? ElementsPublicNetworkManifest.LiquidTestnet
					: throw new InvalidOperationException(InvariantMessage);
		LiquidAssetId planPeggedAssetId = RequireCanonicalAssetId(plan.GetPeggedAssetId());
		if (!StringComparer.Ordinal.Equals(planPeggedAssetId.CanonicalRpcHex, context.PeggedAssetId) ||
			!StringComparer.Ordinal.Equals(context.PeggedAssetId, context.RequiredFeeAssetId))
		{
			throw new InvalidOperationException(InvariantMessage);
		}

		for (int index = 0; index < selected.Length; index++)
		{
			LiquidWalletCoinControlEntry entry = selected[index] ??
				throw new InvalidOperationException(InvariantMessage);
			LiquidTransactionId transactionId = RequireCanonicalTransactionId(
				entry.OutPoint.TransactionId);
			LiquidAssetId entryPeggedAssetId = RequireCanonicalAssetId(entry.PeggedAssetId);
			LiquidAssetId amountAssetId = RequireCanonicalAssetId(entry.Amount.AssetId);
			LiquidAssetId amountPeggedAssetId = RequireCanonicalAssetId(entry.Amount.PeggedAssetId);
			if (transactionId.IsZero ||
				entry.OutPoint.OutputIndex > 0x3fffffff ||
				entryPeggedAssetId != planPeggedAssetId ||
				amountPeggedAssetId != planPeggedAssetId ||
				amountAssetId != entry.Amount.AssetId ||
				entry.Amount.AtomicUnits is < 1 or > LiquidOrdinaryWalletExactSpendPlan.MaximumAtomicUnits ||
				index > 0 && CompareSelected(selected[index - 1], entry) >= 0)
			{
				throw new InvalidOperationException(InvariantMessage);
			}
		}

		addressBytes = 0;
		for (int index = 0; index < destinations.Length; index++)
		{
			LiquidSuppliedConfidentialDestination destination = destinations[index] ??
				throw new InvalidOperationException(InvariantMessage);
			LiquidAddress retainedAddress = destination.GetAddress();
			string address = retainedAddress.GetCanonicalAddressText();
			LiquidAssetId destinationPeggedAssetId = RequireCanonicalAssetId(
				destination.GetPeggedAssetId());
			LiquidAssetId destinationAssetId = RequireCanonicalAssetId(destination.GetAssetId());
			LiquidAssetAmount? amount = destination.GetAmount();
			if (!IsCanonicalRetainedAddress(context, retainedAddress, address) ||
				!StringComparer.Ordinal.Equals(destination.GetNetworkManifestId(), context.ManifestId) ||
				!StringComparer.Ordinal.Equals(retainedAddress.NetworkManifestId, context.ManifestId) ||
				destinationPeggedAssetId != planPeggedAssetId ||
				amount is null ||
				RequireCanonicalAssetId(amount.AssetId) != destinationAssetId ||
				RequireCanonicalAssetId(amount.PeggedAssetId) != planPeggedAssetId ||
				amount.AtomicUnits is < 1 or > LiquidOrdinaryWalletExactSpendPlan.MaximumAtomicUnits ||
				address.Length is < 1 or > LiquidOrdinaryWalletPlanWireLimits.MaximumAddressLength ||
				!IsAscii(address) ||
				!TryCheckedAdd(addressBytes, address.Length, out addressBytes))
			{
				throw new InvalidOperationException(InvariantMessage);
			}
		}

		LiquidAssetAmount explicitFee = plan.GetExplicitFee();
		if (RequireCanonicalAssetId(explicitFee.AssetId) != planPeggedAssetId ||
			RequireCanonicalAssetId(explicitFee.PeggedAssetId) != planPeggedAssetId ||
			explicitFee.AtomicUnits is < 1 or > LiquidOrdinaryWalletExactSpendPlan.MaximumAtomicUnits ||
			!IsExactlyBalanced(selected, destinations, explicitFee))
		{
			throw new InvalidOperationException(InvariantMessage);
		}

		return context;
	}

	private static LiquidAssetId RequireCanonicalAssetId(LiquidAssetId? assetId)
	{
		if (assetId is null || assetId.CanonicalRpcHex is not { } canonicalRpcHex)
		{
			throw new InvalidOperationException(InvariantMessage);
		}

		try
		{
			LiquidAssetId reparsed = LiquidAssetId.ParseRpcHex(canonicalRpcHex);
			return reparsed == assetId
				? reparsed
				: throw new InvalidOperationException(InvariantMessage);
		}
		catch (ArgumentException)
		{
			throw new InvalidOperationException(InvariantMessage);
		}
	}

	private static LiquidTransactionId RequireCanonicalTransactionId(
		LiquidTransactionId? transactionId)
	{
		if (transactionId is null || transactionId.CanonicalRpcHex is not { } canonicalRpcHex)
		{
			throw new InvalidOperationException(InvariantMessage);
		}

		try
		{
			LiquidTransactionId reparsed = LiquidTransactionId.ParseRpcHex(canonicalRpcHex);
			return reparsed == transactionId
				? reparsed
				: throw new InvalidOperationException(InvariantMessage);
		}
		catch (ArgumentException)
		{
			throw new InvalidOperationException(InvariantMessage);
		}
	}

	private static bool IsCanonicalRetainedAddress(
		ElementsPublicNetworkManifest context,
		LiquidAddress retainedAddress,
		string? canonicalText)
	{
		if (canonicalText is null)
		{
			return false;
		}

		try
		{
			LiquidAddress reparsed = LiquidAddress.Parse(context, canonicalText);
			return reparsed.IsConfidential &&
				StringComparer.Ordinal.Equals(reparsed.GetCanonicalAddressText(), canonicalText) &&
				reparsed.Equals(retainedAddress);
		}
		catch (ArgumentException)
		{
			return false;
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static bool IsExactlyBalanced(
		ReadOnlySpan<LiquidWalletCoinControlEntry> selected,
		ReadOnlySpan<LiquidSuppliedConfidentialDestination> destinations,
		LiquidAssetAmount fee)
	{
		for (int selectedIndex = 0; selectedIndex < selected.Length; selectedIndex++)
		{
			var asset = selected[selectedIndex].Amount.AssetId;
			bool appearedEarlier = false;
			for (int earlierIndex = 0; earlierIndex < selectedIndex; earlierIndex++)
			{
				appearedEarlier |= selected[earlierIndex].Amount.AssetId == asset;
			}

			if (appearedEarlier)
			{
				continue;
			}

			long selectedTotal = 0;
			for (int index = selectedIndex; index < selected.Length; index++)
			{
				if (selected[index].Amount.AssetId == asset &&
					!TryCheckedAdd(selectedTotal, selected[index].Amount.AtomicUnits, out selectedTotal))
				{
					return false;
				}
			}

			long requiredTotal = 0;
			for (int index = 0; index < destinations.Length; index++)
			{
				LiquidAssetAmount amount = destinations[index].GetAmount()!;
				if (amount.AssetId == asset &&
					!TryCheckedAdd(requiredTotal, amount.AtomicUnits, out requiredTotal))
				{
					return false;
				}
			}

			if (fee.AssetId == asset && !TryCheckedAdd(requiredTotal, fee.AtomicUnits, out requiredTotal))
			{
				return false;
			}

			if (selectedTotal != requiredTotal)
			{
				return false;
			}
		}

		for (int destinationIndex = 0; destinationIndex < destinations.Length; destinationIndex++)
		{
			var requiredAsset = destinations[destinationIndex].GetAssetId();
			bool found = false;
			for (int selectedIndex = 0; selectedIndex < selected.Length; selectedIndex++)
			{
				found |= selected[selectedIndex].Amount.AssetId == requiredAsset;
			}

			if (!found)
			{
				return false;
			}
		}

		for (int selectedIndex = 0; selectedIndex < selected.Length; selectedIndex++)
		{
			if (selected[selectedIndex].Amount.AssetId == fee.AssetId)
			{
				return true;
			}
		}

		return false;
	}

	private static int CompareSelected(
		LiquidWalletCoinControlEntry left,
		LiquidWalletCoinControlEntry right)
	{
		int transactionOrder = StringComparer.Ordinal.Compare(
			left.OutPoint.TransactionId.CanonicalRpcHex,
			right.OutPoint.TransactionId.CanonicalRpcHex);
		return transactionOrder != 0
			? transactionOrder
			: left.OutPoint.OutputIndex.CompareTo(right.OutPoint.OutputIndex);
	}

	private static bool IsAscii(string value)
	{
		foreach (char character in value)
		{
			if (character > 0x7f)
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsNonzero(ReadOnlySpan<byte> value)
	{
		byte aggregate = 0;
		foreach (byte item in value)
		{
			aggregate |= item;
		}

		return aggregate != 0;
	}

	private static bool Reject(
		LiquidOrdinaryWalletPlanWireErrorCode failure,
		out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
	{
		errorCode = failure;
		return false;
	}

	private static void EnsureCooperation(object? capability)
	{
		if (!ReferenceEquals(capability, CooperationCapability))
		{
			throw new InvalidOperationException(InvariantMessage);
		}
	}

	private static bool TryCheckedAdd(int left, int right, out int result)
	{
		long sum = (long)left + right;
		if (right < 0 || sum > int.MaxValue)
		{
			result = 0;
			return false;
		}

		result = (int)sum;
		return true;
	}

	private static bool TryCheckedAdd(long left, long right, out long result)
	{
		if (right < 0 || left > long.MaxValue - right)
		{
			result = 0;
			return false;
		}

		result = left + right;
		return true;
	}

	private static void Write(ReadOnlySpan<byte> value, Span<byte> destination, ref int cursor)
	{
		value.CopyTo(destination[cursor..]);
		cursor += value.Length;
	}

	private static void WriteUInt16(int value, Span<byte> destination, ref int cursor)
	{
		BinaryPrimitives.WriteUInt16LittleEndian(destination[cursor..], checked((ushort)value));
		cursor += sizeof(ushort);
	}

	private static void WriteUInt32(uint value, Span<byte> destination, ref int cursor)
	{
		BinaryPrimitives.WriteUInt32LittleEndian(destination[cursor..], value);
		cursor += sizeof(uint);
	}

	private static void WriteUInt64(ulong value, Span<byte> destination, ref int cursor)
	{
		BinaryPrimitives.WriteUInt64LittleEndian(destination[cursor..], value);
		cursor += sizeof(ulong);
	}

	private static void WriteLowerHex(string canonicalHex, Span<byte> destination, ref int cursor)
	{
		if (canonicalHex.Length != 64)
		{
			throw new InvalidOperationException(InvariantMessage);
		}

		for (int index = 0; index < 32; index++)
		{
			int high = ParseLowerHexNibble(canonicalHex[index * 2]);
			int low = ParseLowerHexNibble(canonicalHex[index * 2 + 1]);
			if (high < 0 || low < 0)
			{
				throw new InvalidOperationException(InvariantMessage);
			}

			destination[cursor + index] = (byte)((high << 4) | low);
		}

		cursor += 32;
	}

	private static int ParseLowerHexNibble(char value) =>
		value switch
		{
			>= '0' and <= '9' => value - '0',
			>= 'a' and <= 'f' => value - 'a' + 10,
			_ => -1,
		};
}
