using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Rpc;
using WalletWasabi.Liquid.Transactions;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// MANAGED-WALLET-UI-SEND-EXECUTE-001 (V2 section 3): the internal sealed per-call execution
/// scope. It owns the replay-protection key, the external wallet-network context, the fresh
/// 32-byte source epoch, the public spend descriptor copy, and the SLIP-77 master as mutable
/// byte arrays; it references the real caller-owned key owner and the shared application RPC
/// client without owning them. Disposal zeroizes every owned mutable byte array and is
/// idempotent. It never disposes the shared <see cref="ElementsRpcClient"/>; that ownership is
/// explicit. The scope is created after argument validation and disposed exactly once in
/// <c>finally</c> by the executor.
/// </summary>
internal sealed class LiquidWalletSendExecutionScope : ILiquidWalletSendExecutionScope
{
	private readonly Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<ElementsExpectationBoundRawTransactionBatch>> _acquireFundingSource;
	private readonly Func<string, CancellationToken, Task> _scheduleRefresh;
	private int _disposed;

	internal LiquidWalletSendExecutionScope(
		byte[] replayProtectionKey,
		byte[] externalWalletNetworkContext,
		byte[] sourceEpoch,
		ILiquidWalletSigner keyOwner,
		byte[] descriptorBytes,
		ulong lastIndex,
		byte[] slip77MasterKey,
		ElementsRpcClient rpcClient,
		string expectedEffectiveFeeAsset,
		string walletDataDirectory,
		Func<LiquidWalletUiSendExecutionRequest, CancellationToken, Task<ElementsExpectationBoundRawTransactionBatch>> acquireFundingSource,
		Func<string, CancellationToken, Task> scheduleRefresh)
	{
		ArgumentNullException.ThrowIfNull(replayProtectionKey);
		ArgumentNullException.ThrowIfNull(externalWalletNetworkContext);
		ArgumentNullException.ThrowIfNull(sourceEpoch);
		ArgumentNullException.ThrowIfNull(keyOwner);
		ArgumentNullException.ThrowIfNull(descriptorBytes);
		ArgumentNullException.ThrowIfNull(slip77MasterKey);
		ArgumentNullException.ThrowIfNull(rpcClient);
		ArgumentException.ThrowIfNullOrEmpty(expectedEffectiveFeeAsset);
		ArgumentException.ThrowIfNullOrEmpty(walletDataDirectory);
		ArgumentNullException.ThrowIfNull(acquireFundingSource);
		ArgumentNullException.ThrowIfNull(scheduleRefresh);
		if (sourceEpoch.Length != 32)
		{
			throw new ArgumentException("A Liquid send execution source epoch must be exactly 32 bytes.", nameof(sourceEpoch));
		}

		ReplayProtectionKey = replayProtectionKey;
		ExternalWalletNetworkContext = externalWalletNetworkContext;
		SourceEpoch = sourceEpoch;
		KeyOwner = keyOwner;
		DescriptorBytes = descriptorBytes;
		DescriptorString = Encoding.UTF8.GetString(descriptorBytes);
		LastIndex = lastIndex;
		Slip77MasterKey = slip77MasterKey;
		RpcClient = rpcClient;
		ExpectedEffectiveFeeAsset = expectedEffectiveFeeAsset;
		WalletDataDirectory = walletDataDirectory;
		_acquireFundingSource = acquireFundingSource;
		_scheduleRefresh = scheduleRefresh;

		// The scope owns the call-scoped signer: construct it here and dispose it in
		// Dispose after the owned byte arrays are zeroized (V2 section 3 / amendment 6).
		Signer = LiquidWalletNativeSigner.Create(keyOwner, DescriptorString, lastIndex, slip77MasterKey);
	}

	public byte[] ReplayProtectionKey { get; }
	public byte[] ExternalWalletNetworkContext { get; }
	public byte[] SourceEpoch { get; }
	public ILiquidWalletSigner KeyOwner { get; }
	public LiquidWalletNativeSigner Signer { get; }
	public byte[] DescriptorBytes { get; }
	public string DescriptorString { get; }
	public ulong LastIndex { get; }
	public byte[] Slip77MasterKey { get; }
	public ElementsRpcClient RpcClient { get; }
	public string ExpectedEffectiveFeeAsset { get; }

	/// <summary>
	/// The single source of truth for the wallet's landed state directory, taken from the
	/// authenticated session. The send request carries no directory copy; the executor loads
	/// state from this session-supplied directory.
	/// </summary>
	public string WalletDataDirectory { get; }

	public Task<ElementsExpectationBoundRawTransactionBatch> AcquireFundingSourceAsync(
		LiquidWalletUiSendExecutionRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		return _acquireFundingSource(request, cancellationToken);
	}

	public Task ScheduleRefreshAsync(
		string canonicalTransactionIdHex,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrEmpty(canonicalTransactionIdHex);
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		return _scheduleRefresh(canonicalTransactionIdHex, cancellationToken);
	}

	/// <summary>
	/// Zeroizes every owned mutable byte array (the replay key/context, the source epoch, the
	/// SLIP-77 copy, and the descriptor copy). Idempotent. Never disposes the shared RPC client
	/// or the caller-owned key owner.
	/// </summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		CryptographicOperations.ZeroMemory(ReplayProtectionKey);
		CryptographicOperations.ZeroMemory(ExternalWalletNetworkContext);
		CryptographicOperations.ZeroMemory(SourceEpoch);
		CryptographicOperations.ZeroMemory(DescriptorBytes);
		CryptographicOperations.ZeroMemory(Slip77MasterKey);
		Signer.Dispose();
	}
}
