using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.Wallet.Wire;

internal static partial class LiquidOrdinaryWalletPlanEncoder
{
	internal sealed class LiquidOrdinaryWalletPlanFundingRow : IDisposable
	{
		private const string DisposedMessage = "Liquid ordinary-wallet plan funding row is disposed.";

		private readonly object _gate = new();
		private readonly byte[] _candidateTransaction;
		private readonly byte[][] _previousTransactions;
		private bool _disposed;

		private LiquidOrdinaryWalletPlanFundingRow(
			byte[] candidateTransaction,
			byte[][] previousTransactions)
		{
			_candidateTransaction = candidateTransaction;
			_previousTransactions = previousTransactions;
		}

		/// <summary>
		/// Creates an independently owned copy of source transaction bytes. These bytes are not parsed
		/// here and do not bind the selected confidential commitment's actual asset or value; native
		/// semantic preparation remains the funding authority. Deterministic disposal is required to
		/// clear the owned copies.
		/// </summary>
		internal static bool TryCreate(
			byte[]? candidateTransaction,
			IReadOnlyList<byte[]?>? previousTransactions,
			out LiquidOrdinaryWalletPlanFundingRow? row,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
		{
			row = null;
			errorCode = LiquidOrdinaryWalletPlanWireErrorCode.None;

			if (candidateTransaction is null || previousTransactions is null)
			{
				return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
			}

			int previousCount = previousTransactions.Count;
			if (previousCount < 0)
			{
				return Reject(LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded, out errorCode);
			}

			for (int index = 0; index < previousCount; index++)
			{
				if (previousTransactions[index] is null)
				{
					return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
				}
			}

			for (int index = 0; index < previousCount; index++)
			{
				byte[]? previous = previousTransactions[index];
				if (previous is null)
				{
					return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
				}
			}

			if (candidateTransaction.Length is 0 or > LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength ||
				previousCount > LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount)
			{
				return Reject(LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded, out errorCode);
			}

			byte[]?[]? sourcePrevious = null;
			byte[]? ownedCandidate = null;
			byte[][]? ownedPrevious = null;
			LiquidOrdinaryWalletPlanFundingRow? cleanupOwner = null;
			try
			{
				sourcePrevious = new byte[]?[previousCount];
				for (int index = 0; index < previousCount; index++)
				{
					byte[]? previous = previousTransactions[index];
					if (previous is null)
					{
						return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
					}

					sourcePrevious[index] = previous;
				}

				long aggregateBytes = candidateTransaction.Length;
				for (int index = 0; index < previousCount; index++)
				{
					byte[] previous = sourcePrevious[index]!;
					if (previous.Length is 0 or > LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength ||
						!TryCheckedAdd(aggregateBytes, previous.Length, out aggregateBytes) ||
						aggregateBytes > LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength)
					{
						return Reject(LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded, out errorCode);
					}
				}

				for (int index = 1; index < previousCount; index++)
				{
					if (sourcePrevious[index - 1]!.AsSpan().SequenceCompareTo(sourcePrevious[index]) >= 0)
					{
						return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding, out errorCode);
					}
				}

				ownedCandidate = candidateTransaction.ToArray();
				ownedPrevious = new byte[previousCount][];
				for (int index = 0; index < previousCount; index++)
				{
					ownedPrevious[index] = sourcePrevious[index]!.ToArray();
				}

				if (!HasCanonicalOwnedShape(ownedCandidate, ownedPrevious))
				{
					return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidEncoding, out errorCode);
				}

				cleanupOwner = new LiquidOrdinaryWalletPlanFundingRow(ownedCandidate, ownedPrevious);
				ownedCandidate = null;
				ownedPrevious = null;
				Array.Clear(sourcePrevious);
				sourcePrevious = null;
				errorCode = LiquidOrdinaryWalletPlanWireErrorCode.None;
				row = cleanupOwner;
				cleanupOwner = null;
				return true;
			}
			finally
			{
				if (sourcePrevious is not null)
				{
					Array.Clear(sourcePrevious);
				}

				cleanupOwner?.Dispose();
				Clear(ownedCandidate, ownedPrevious);
			}
		}

		internal void EnsureNotDisposed(object? capability)
		{
			EnsureCooperation(capability);
			lock (_gate)
			{
				ThrowIfDisposed();
			}
		}

		internal EncodingShape GetEncodingShape(object? capability)
		{
			EnsureCooperation(capability);
			lock (_gate)
			{
				ThrowIfDisposed();
				if (!HasCanonicalOwnedShape(_candidateTransaction, _previousTransactions))
				{
					throw new InvalidOperationException(LiquidOrdinaryWalletPlanEncoder.InvariantMessage);
				}

				long aggregateBytes = _candidateTransaction.Length;
				foreach (byte[] previous in _previousTransactions)
				{
					aggregateBytes = checked(aggregateBytes + previous.Length);
				}

				return new EncodingShape(
					_candidateTransaction.Length,
					_previousTransactions.Length,
					aggregateBytes);
			}
		}

		internal LiquidOrdinaryWalletPlanFundingRow CreateOwnedCopy(object? capability)
		{
			EnsureCooperation(capability);
			lock (_gate)
			{
				ThrowIfDisposed();
				byte[]? candidate = null;
				byte[][]? previous = null;
				try
				{
					candidate = _candidateTransaction.ToArray();
					previous = new byte[_previousTransactions.Length][];
					for (int index = 0; index < previous.Length; index++)
					{
						previous[index] = _previousTransactions[index].ToArray();
					}

					var copy = new LiquidOrdinaryWalletPlanFundingRow(candidate, previous);
					candidate = null;
					previous = null;
					return copy;
				}
				finally
				{
					Clear(candidate, previous);
				}
			}
		}

		internal void WritePayloads(object? capability, byte[] frame, ref int cursor)
		{
			EnsureCooperation(capability);
			lock (_gate)
			{
				ThrowIfDisposed();
				_candidateTransaction.CopyTo(frame.AsSpan(cursor));
				cursor += _candidateTransaction.Length;
				foreach (byte[] previous in _previousTransactions)
				{
					BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(cursor), checked((uint)previous.Length));
					cursor += sizeof(uint);
					previous.CopyTo(frame.AsSpan(cursor));
					cursor += previous.Length;
				}
			}
		}

		public void Dispose()
		{
			lock (_gate)
			{
				if (_disposed)
				{
					return;
				}

				_disposed = true;
				Clear(_candidateTransaction, _previousTransactions);
			}
		}

		public override string ToString() => nameof(LiquidOrdinaryWalletPlanFundingRow);

		private static bool HasCanonicalOwnedShape(byte[] candidate, byte[][] previous)
		{
			if (candidate.Length is 0 or > LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength ||
				previous.Length > LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount)
			{
				return false;
			}

			long aggregateBytes = candidate.Length;
			for (int index = 0; index < previous.Length; index++)
			{
				byte[] payload = previous[index];
				if (payload is null ||
					payload.Length is 0 or > LiquidOrdinaryWalletPlanWireLimits.MaximumTransactionLength ||
					!TryCheckedAdd(aggregateBytes, payload.Length, out aggregateBytes) ||
					aggregateBytes > LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength ||
					index > 0 && previous[index - 1].AsSpan().SequenceCompareTo(payload) >= 0)
				{
					return false;
				}
			}

			return true;
		}

		private static void Clear(byte[]? candidate, byte[][]? previous)
		{
			if (candidate is not null)
			{
				CryptographicOperations.ZeroMemory(candidate);
			}

			if (previous is null)
			{
				return;
			}

			foreach (byte[]? payload in previous)
			{
				if (payload is not null)
				{
					CryptographicOperations.ZeroMemory(payload);
				}
			}

			Array.Clear(previous);
		}

		private static bool Reject(
			LiquidOrdinaryWalletPlanWireErrorCode failure,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
		{
			errorCode = failure;
			return false;
		}

		private static bool TryCheckedAdd(long left, int right, out long result)
		{
			if (right < 0 || left > long.MaxValue - right)
			{
				result = 0;
				return false;
			}

			result = left + right;
			return true;
		}

		private void ThrowIfDisposed()
		{
			if (_disposed)
			{
				throw new ObjectDisposedException(
					nameof(LiquidOrdinaryWalletPlanFundingRow),
					DisposedMessage);
			}
		}

		internal readonly struct EncodingShape(
			int candidateLength,
			int previousCount,
			long aggregateTransactionLength)
		{
			internal int CandidateLength { get; } = candidateLength;
			internal int PreviousCount { get; } = previousCount;
			internal long AggregateTransactionLength { get; } = aggregateTransactionLength;
		}
	}
}
