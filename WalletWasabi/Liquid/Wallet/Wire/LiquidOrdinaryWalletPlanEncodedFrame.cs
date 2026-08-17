using System.Security.Cryptography;

namespace WalletWasabi.Liquid.Wallet.Wire;

internal static partial class LiquidOrdinaryWalletPlanEncoder
{
	internal sealed class LiquidOrdinaryWalletPlanEncodedFrame : IDisposable
	{
		private const string DisposedMessage = "Liquid ordinary-wallet plan encoded frame is disposed.";
		private const string DestinationLengthMessage =
			"An exact Liquid ordinary-wallet plan wire frame destination is required.";

		private readonly object _gate = new();
		private readonly byte[] _frame;
		private bool _disposed;

		private LiquidOrdinaryWalletPlanEncodedFrame(byte[] ownedFrame)
		{
			_frame = ownedFrame;
		}

		internal static LiquidOrdinaryWalletPlanEncodedFrame TakeOwnership(
			object? capability,
			ref byte[]? frame)
		{
			EnsureCooperation(capability);
			byte[] ownedFrame = frame ??
				throw new InvalidOperationException(LiquidOrdinaryWalletPlanEncoder.InvariantMessage);
			var owner = new LiquidOrdinaryWalletPlanEncodedFrame(ownedFrame);
			frame = null;
			return owner;
		}

		internal int Length
		{
			get
			{
				lock (_gate)
				{
					ThrowIfDisposed();
					return _frame.Length;
				}
			}
		}

		/// <summary>
		/// Copies the complete frame into an exact caller-owned destination.
		/// The caller must clear every destination copy separately; disposing this owner cannot erase copies outside it. The frame's
		/// source epoch is plaintext and provides neither authentication nor replay protection.
		/// </summary>
		internal void CopyFrameTo(Span<byte> exactDestination)
		{
			lock (_gate)
			{
				ThrowIfDisposed();
				if (exactDestination.Length != _frame.Length)
				{
					throw new ArgumentException(DestinationLengthMessage, nameof(exactDestination));
				}

				_frame.AsSpan().CopyTo(exactDestination);
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
				CryptographicOperations.ZeroMemory(_frame);
			}
		}

		public override string ToString() => nameof(LiquidOrdinaryWalletPlanEncodedFrame);

		private void ThrowIfDisposed()
		{
			if (_disposed)
			{
				throw new ObjectDisposedException(
					nameof(LiquidOrdinaryWalletPlanEncodedFrame),
					DisposedMessage);
			}
		}
	}
}
