namespace WalletWasabi.Liquid.Wallet.Wire;

internal static partial class LiquidOrdinaryWalletPlanEncoder
{
	internal sealed class LiquidOrdinaryWalletPlanFundingBatch : IDisposable
	{
		private const string DisposedMessage = "Liquid ordinary-wallet plan funding batch is disposed.";

		private readonly object _gate = new();
		private readonly LiquidOrdinaryWalletPlanFundingRow[] _rows;
		private LiquidOrdinaryWalletExactSpendPlan? _plan;
		private bool _disposed;

		private LiquidOrdinaryWalletPlanFundingBatch(
			LiquidOrdinaryWalletExactSpendPlan plan,
			LiquidOrdinaryWalletPlanFundingRow[] rows)
		{
			_plan = plan;
			_rows = rows;
		}

		/// <summary>
		/// Creates one index-preserving defensive row copy for every selected plan entry. The copied
		/// funding remains unvalidated transaction material and does not establish actual confidential
		/// asset or value equality. Deterministic disposal clears every owned row payload.
		/// </summary>
		internal static bool TryCreate(
			LiquidOrdinaryWalletExactSpendPlan? plan,
			IReadOnlyList<LiquidOrdinaryWalletPlanFundingRow?>? rows,
			out LiquidOrdinaryWalletPlanFundingBatch? batch,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
		{
			batch = null;
			errorCode = LiquidOrdinaryWalletPlanWireErrorCode.None;

			if (plan is null || rows is null)
			{
				return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
			}

			int rowCount = rows.Count;
			if (rowCount < 0)
			{
				return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
			}

			for (int index = 0; index < rowCount; index++)
			{
				if (rows[index] is null)
				{
					return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
				}
			}

			for (int index = 0; index < rowCount; index++)
			{
				LiquidOrdinaryWalletPlanFundingRow? sourceRow = rows[index];
				if (sourceRow is null)
				{
					return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
				}

				sourceRow.EnsureNotDisposed(CooperationCapability);
			}

			if (rowCount != plan.SelectedInputCount ||
				rowCount > LiquidOrdinaryWalletExactSpendPlan.MaximumSelectedInputCount)
			{
				return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
			}

			LiquidOrdinaryWalletPlanFundingRow?[]? sourceRows = null;
			LiquidOrdinaryWalletPlanFundingRow[]? ownedRows = null;
			LiquidOrdinaryWalletPlanFundingBatch? cleanupOwner = null;
			int copiedCount = 0;
			try
			{
				sourceRows = new LiquidOrdinaryWalletPlanFundingRow?[rowCount];
				for (int index = 0; index < rowCount; index++)
				{
					LiquidOrdinaryWalletPlanFundingRow? sourceRow = rows[index];
					if (sourceRow is null)
					{
						return Reject(LiquidOrdinaryWalletPlanWireErrorCode.InvalidArgument, out errorCode);
					}

					sourceRow.EnsureNotDisposed(CooperationCapability);
					sourceRows[index] = sourceRow;
				}

				int aggregatePreviousCount = 0;
				long aggregateTransactionLength = 0;
				for (int index = 0; index < rowCount; index++)
				{
					LiquidOrdinaryWalletPlanFundingRow.EncodingShape shape =
						sourceRows[index]!.GetEncodingShape(CooperationCapability);
					if (!TryCheckedAdd(aggregatePreviousCount, shape.PreviousCount, out aggregatePreviousCount) ||
						aggregatePreviousCount > LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount ||
						!TryCheckedAdd(
							aggregateTransactionLength,
							shape.AggregateTransactionLength,
							out aggregateTransactionLength) ||
						aggregateTransactionLength > LiquidOrdinaryWalletPlanWireLimits.MaximumAggregateTransactionLength)
					{
						return Reject(LiquidOrdinaryWalletPlanWireErrorCode.LimitExceeded, out errorCode);
					}
				}

				ownedRows = new LiquidOrdinaryWalletPlanFundingRow[rowCount];
				while (copiedCount < rowCount)
				{
					LiquidOrdinaryWalletPlanFundingRow copiedRow =
						sourceRows[copiedCount]!.CreateOwnedCopy(CooperationCapability);
					ownedRows[copiedCount++] = copiedRow;
				}

				cleanupOwner = new LiquidOrdinaryWalletPlanFundingBatch(plan, ownedRows);
				ownedRows = null;
				copiedCount = 0;
				Array.Clear(sourceRows);
				sourceRows = null;
				errorCode = LiquidOrdinaryWalletPlanWireErrorCode.None;
				batch = cleanupOwner;
				cleanupOwner = null;
				return true;
			}
			finally
			{
				if (sourceRows is not null)
				{
					Array.Clear(sourceRows);
				}

				cleanupOwner?.Dispose();
				for (int index = 0; index < copiedCount && ownedRows is not null; index++)
				{
					ownedRows[index].Dispose();
				}
			}
		}

		internal bool TryEncode(
			object? capability,
			ReadOnlySpan<byte> sourceEpoch,
			LiquidOrdinaryWalletExactSpendPlan plan,
			out LiquidOrdinaryWalletPlanEncodedFrame? frame,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
		{
			EnsureCooperation(capability);
			lock (_gate)
			{
				ThrowIfDisposed();
				return LiquidOrdinaryWalletPlanEncoder.TryEncodeLocked(
					sourceEpoch,
					plan,
					_plan,
					_rows,
					out frame,
					out errorCode);
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
				_plan = null;
				foreach (LiquidOrdinaryWalletPlanFundingRow row in _rows)
				{
					row.Dispose();
				}

				Array.Clear(_rows);
			}
		}

		public override string ToString() => nameof(LiquidOrdinaryWalletPlanFundingBatch);

		private static bool Reject(
			LiquidOrdinaryWalletPlanWireErrorCode failure,
			out LiquidOrdinaryWalletPlanWireErrorCode errorCode)
		{
			errorCode = failure;
			return false;
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

		private void ThrowIfDisposed()
		{
			if (_disposed)
			{
				throw new ObjectDisposedException(
					nameof(LiquidOrdinaryWalletPlanFundingBatch),
					DisposedMessage);
			}
		}
	}
}
