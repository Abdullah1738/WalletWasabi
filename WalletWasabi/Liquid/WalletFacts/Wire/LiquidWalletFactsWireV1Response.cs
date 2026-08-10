using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WalletWasabi.Liquid.WalletFacts.Wire;

internal enum LiquidWalletFactsWireV1Branch : byte
{
	External = 0,
	Internal = 1,
}

internal sealed class LiquidWalletFactsWireV1Response : IDisposable
{
	private const int SourceEpochOffset = 32;
	private const int SourceEpochLength = 32;
	private const int AggregateOwnedOutputCountOffset = 24;
	private const int TransactionIdLength = 32;
	private const int WitnessBindingOffset = 32;
	private const int WitnessBindingLength = 32;
	private const int TransactionInputCountOffset = 64;
	private const int TransactionOwnedOutputCountOffset = 68;
	private const int TransactionFixedLength = 72;
	private const int InputLength = 36;
	private const int OwnedOutputLength = 144;
	private const int PreviousOutputIndexOffset = 32;
	private const int OutputIndexOffset = 0;
	private const int SpendPublicKeyOffset = 8;
	private const int PublicKeyLength = 33;
	private const int BlindingPublicKeyOffset = 41;
	private const int BranchOffset = 74;
	private const int DerivationIndexOffset = 78;
	private const int AssetIdOffset = 82;
	private const int AssetIdLength = 32;
	private const int ValueOffset = 114;
	private const int ScriptPubKeyOffset = 122;
	private const int ScriptPubKeyLength = 22;
	private const string DisposedMessage = "Liquid wallet facts wire response is disposed.";
	private const string IndexMessage = "A valid wallet facts wire index is required.";

	private readonly object _gate = new();
	private readonly byte[] _frame;
	private readonly int[] _transactionOffsets;
	private bool _disposed;

	internal LiquidWalletFactsWireV1Response(byte[] ownedFrame, int[] transactionOffsets)
	{
		_frame = ownedFrame;
		_transactionOffsets = transactionOffsets;
	}

	public int TransactionCount
	{
		get
		{
			lock (_gate)
			{
				ThrowIfDisposed();
				return _transactionOffsets.Length;
			}
		}
	}

	public int OwnedOutputCount
	{
		get
		{
			lock (_gate)
			{
				ThrowIfDisposed();
				return ReadInt32(AggregateOwnedOutputCountOffset);
			}
		}
	}

	public bool IsEmpty
	{
		get
		{
			lock (_gate)
			{
				ThrowIfDisposed();
				return _transactionOffsets.Length == 0;
			}
		}
	}

	public byte[] GetSourceEpoch()
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return CopyBytes(SourceEpochOffset, SourceEpochLength);
		}
	}

	public LiquidWalletFactsWireV1TransactionView GetTransaction(int index)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			ValidateIndex(index, _transactionOffsets.Length);
			return new LiquidWalletFactsWireV1TransactionView(this, index);
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
			Array.Clear(_transactionOffsets);
		}
	}

	public override string ToString() => nameof(LiquidWalletFactsWireV1Response);

	private void ValidateTransactionIndex(int transactionIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			ValidateIndex(transactionIndex, _transactionOffsets.Length);
		}
	}

	private void ValidateInputIndex(int transactionIndex, int inputIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			int transactionOffset = GetTransactionOffset(transactionIndex);
			ValidateIndex(inputIndex, ReadInt32(transactionOffset + TransactionInputCountOffset));
		}
	}

	private void ValidateOwnedOutputIndex(int transactionIndex, int outputIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			int transactionOffset = GetTransactionOffset(transactionIndex);
			ValidateIndex(outputIndex, ReadInt32(transactionOffset + TransactionOwnedOutputCountOffset));
		}
	}

	private LiquidWalletFactsWireV1InputView CreateInputView(int transactionIndex, int inputIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return new LiquidWalletFactsWireV1InputView(this, transactionIndex, inputIndex);
		}
	}

	private LiquidWalletFactsWireV1OwnedOutputView CreateOwnedOutputView(int transactionIndex, int outputIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return new LiquidWalletFactsWireV1OwnedOutputView(this, transactionIndex, outputIndex);
		}
	}

	private int GetInputCount(int transactionIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return ReadInt32(GetTransactionOffset(transactionIndex) + TransactionInputCountOffset);
		}
	}

	private int GetOwnedOutputCount(int transactionIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return ReadInt32(GetTransactionOffset(transactionIndex) + TransactionOwnedOutputCountOffset);
		}
	}

	private byte[] GetTransactionIdConsensusBytes(int transactionIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return CopyBytes(GetTransactionOffset(transactionIndex), TransactionIdLength);
		}
	}

	private byte[] GetTransactionWitnessBinding(int transactionIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return CopyBytes(GetTransactionOffset(transactionIndex) + WitnessBindingOffset, WitnessBindingLength);
		}
	}

	private byte[] GetPreviousTransactionIdConsensusBytes(int transactionIndex, int inputIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return CopyBytes(GetInputOffset(transactionIndex, inputIndex), TransactionIdLength);
		}
	}

	private uint GetPreviousOutputIndex(int transactionIndex, int inputIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return ReadUInt32(GetInputOffset(transactionIndex, inputIndex) + PreviousOutputIndexOffset);
		}
	}

	private uint GetOutputIndex(int transactionIndex, int outputIndex) =>
		ReadOwnedOutputUInt32(transactionIndex, outputIndex, OutputIndexOffset);

	private LiquidWalletFactsWireV1Branch GetBranch(int transactionIndex, int outputIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return (LiquidWalletFactsWireV1Branch)_frame[GetOwnedOutputOffset(transactionIndex, outputIndex) + BranchOffset];
		}
	}

	private uint GetDerivationIndex(int transactionIndex, int outputIndex) =>
		ReadOwnedOutputUInt32(transactionIndex, outputIndex, DerivationIndexOffset);

	private ulong GetValue(int transactionIndex, int outputIndex)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return ReadUInt64(GetOwnedOutputOffset(transactionIndex, outputIndex) + ValueOffset);
		}
	}

	private byte[] GetScriptPubKey(int transactionIndex, int outputIndex) =>
		CopyOwnedOutputBytes(transactionIndex, outputIndex, ScriptPubKeyOffset, ScriptPubKeyLength);

	private byte[] GetSpendPublicKey(int transactionIndex, int outputIndex) =>
		CopyOwnedOutputBytes(transactionIndex, outputIndex, SpendPublicKeyOffset, PublicKeyLength);

	private byte[] GetBlindingPublicKey(int transactionIndex, int outputIndex) =>
		CopyOwnedOutputBytes(transactionIndex, outputIndex, BlindingPublicKeyOffset, PublicKeyLength);

	private byte[] GetAssetIdConsensusBytes(int transactionIndex, int outputIndex) =>
		CopyOwnedOutputBytes(transactionIndex, outputIndex, AssetIdOffset, AssetIdLength);

	private uint ReadOwnedOutputUInt32(int transactionIndex, int outputIndex, int fieldOffset)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return ReadUInt32(GetOwnedOutputOffset(transactionIndex, outputIndex) + fieldOffset);
		}
	}

	private byte[] CopyOwnedOutputBytes(int transactionIndex, int outputIndex, int fieldOffset, int length)
	{
		lock (_gate)
		{
			ThrowIfDisposed();
			return CopyBytes(GetOwnedOutputOffset(transactionIndex, outputIndex) + fieldOffset, length);
		}
	}

	private int GetInputOffset(int transactionIndex, int inputIndex)
	{
		int transactionOffset = GetTransactionOffset(transactionIndex);
		int inputCount = ReadInt32(transactionOffset + TransactionInputCountOffset);
		ValidateIndex(inputIndex, inputCount);
		return transactionOffset + TransactionFixedLength + (inputIndex * InputLength);
	}

	private int GetOwnedOutputOffset(int transactionIndex, int outputIndex)
	{
		int transactionOffset = GetTransactionOffset(transactionIndex);
		int inputCount = ReadInt32(transactionOffset + TransactionInputCountOffset);
		int outputCount = ReadInt32(transactionOffset + TransactionOwnedOutputCountOffset);
		ValidateIndex(outputIndex, outputCount);
		return transactionOffset + TransactionFixedLength + (inputCount * InputLength) + (outputIndex * OwnedOutputLength);
	}

	private int GetTransactionOffset(int transactionIndex)
	{
		ValidateIndex(transactionIndex, _transactionOffsets.Length);
		return _transactionOffsets[transactionIndex];
	}

	private byte[] CopyBytes(int offset, int length) => _frame.AsSpan(offset, length).ToArray();

	private int ReadInt32(int offset) => checked((int)ReadUInt32(offset));

	private uint ReadUInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_frame.AsSpan(offset, sizeof(uint)));

	private ulong ReadUInt64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(_frame.AsSpan(offset, sizeof(ulong)));

	private void ThrowIfDisposed()
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(LiquidWalletFactsWireV1Response), DisposedMessage);
		}
	}

	private static void ValidateIndex(int index, int count)
	{
		if ((uint)index >= (uint)count)
		{
			throw new ArgumentOutOfRangeException("index", IndexMessage);
		}
	}

	internal sealed class LiquidWalletFactsWireV1TransactionView
	{
		private readonly LiquidWalletFactsWireV1Response _owner;
		private readonly int _transactionIndex;

		internal LiquidWalletFactsWireV1TransactionView(LiquidWalletFactsWireV1Response owner, int transactionIndex)
		{
			_owner = owner ?? throw new ArgumentNullException(nameof(owner));
			_owner.ValidateTransactionIndex(transactionIndex);
			_transactionIndex = transactionIndex;
		}

		public int InputCount => _owner.GetInputCount(_transactionIndex);

		public int OwnedOutputCount => _owner.GetOwnedOutputCount(_transactionIndex);

		public byte[] GetTransactionIdConsensusBytes() => _owner.GetTransactionIdConsensusBytes(_transactionIndex);

		public byte[] GetTransactionWitnessBinding() => _owner.GetTransactionWitnessBinding(_transactionIndex);

		public LiquidWalletFactsWireV1InputView GetInput(int index) =>
			_owner.CreateInputView(_transactionIndex, index);

		public LiquidWalletFactsWireV1OwnedOutputView GetOwnedOutput(int index) =>
			_owner.CreateOwnedOutputView(_transactionIndex, index);

		public override string ToString() => nameof(LiquidWalletFactsWireV1TransactionView);
	}

	internal sealed class LiquidWalletFactsWireV1InputView
	{
		private readonly LiquidWalletFactsWireV1Response _owner;
		private readonly int _transactionIndex;
		private readonly int _inputIndex;

		internal LiquidWalletFactsWireV1InputView(
			LiquidWalletFactsWireV1Response owner,
			int transactionIndex,
			int inputIndex)
		{
			_owner = owner ?? throw new ArgumentNullException(nameof(owner));
			_owner.ValidateInputIndex(transactionIndex, inputIndex);
			_transactionIndex = transactionIndex;
			_inputIndex = inputIndex;
		}

		public byte[] GetPreviousTransactionIdConsensusBytes() =>
			_owner.GetPreviousTransactionIdConsensusBytes(_transactionIndex, _inputIndex);

		public uint PreviousOutputIndex => _owner.GetPreviousOutputIndex(_transactionIndex, _inputIndex);

		public override string ToString() => nameof(LiquidWalletFactsWireV1InputView);
	}

	internal sealed class LiquidWalletFactsWireV1OwnedOutputView
	{
		private readonly LiquidWalletFactsWireV1Response _owner;
		private readonly int _transactionIndex;
		private readonly int _outputIndex;

		internal LiquidWalletFactsWireV1OwnedOutputView(
			LiquidWalletFactsWireV1Response owner,
			int transactionIndex,
			int outputIndex)
		{
			_owner = owner ?? throw new ArgumentNullException(nameof(owner));
			_owner.ValidateOwnedOutputIndex(transactionIndex, outputIndex);
			_transactionIndex = transactionIndex;
			_outputIndex = outputIndex;
		}

		public uint OutputIndex => _owner.GetOutputIndex(_transactionIndex, _outputIndex);

		public LiquidWalletFactsWireV1Branch Branch => _owner.GetBranch(_transactionIndex, _outputIndex);

		public uint DerivationIndex => _owner.GetDerivationIndex(_transactionIndex, _outputIndex);

		public ulong Value => _owner.GetValue(_transactionIndex, _outputIndex);

		public byte[] GetScriptPubKey() => _owner.GetScriptPubKey(_transactionIndex, _outputIndex);

		public byte[] GetSpendPublicKey() => _owner.GetSpendPublicKey(_transactionIndex, _outputIndex);

		public byte[] GetBlindingPublicKey() => _owner.GetBlindingPublicKey(_transactionIndex, _outputIndex);

		public byte[] GetAssetIdConsensusBytes() => _owner.GetAssetIdConsensusBytes(_transactionIndex, _outputIndex);

		public override string ToString() => nameof(LiquidWalletFactsWireV1OwnedOutputView);
	}
}
