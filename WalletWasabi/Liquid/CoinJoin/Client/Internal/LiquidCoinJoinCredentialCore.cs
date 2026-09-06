using System.Collections.Immutable;
using WabiSabi;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;

namespace WalletWasabi.Liquid.CoinJoin.Client.Internal;

internal sealed record LiquidCredentialSnapshot(long Value, string Randomness, string MacT, string MacV);

internal sealed class LiquidCoinJoinCredentialCore
{
	private const long MaxAmount = (1L << 51) - 1;
	private readonly object _gate = new();
	private readonly WabiSabiClient _client;
	private readonly long _maxAmount;
	private Credential[] _credentials = [];
	private CredentialsResponseValidation? _validation;
	private ImmutableArray<LiquidCredentialSnapshot> _presented;
	private ImmutableArray<(long Value, string Randomness, string Ma)> _requested;
	private Guid _operationId;
	private Guid? _consumptionResult;
	private bool _consuming;
	private bool _terminal;

	public LiquidCoinJoinCredentialCore(CredentialIssuerParameters issuerParameters, WasabiRandom random, long maxAmount = MaxAmount)
	{
		ArgumentNullException.ThrowIfNull(issuerParameters);
		ArgumentNullException.ThrowIfNull(random);
		if (maxAmount <= 0 || maxAmount > MaxAmount) throw new ArgumentOutOfRangeException(nameof(maxAmount));
		_maxAmount = maxAmount;
		_client = new WabiSabiClient(issuerParameters, random, maxAmount);
	}

	public ImmutableArray<LiquidCredentialSnapshot> Credentials
	{
		get { lock (_gate) return _credentials.Select(Snapshot).ToImmutableArray(); }
	}

	public (Guid OperationId, ICredentialsRequest Request) CreateIssuance(long amount) => CreateOperation(amount, consuming: false);

	public (Guid OperationId, ICredentialsRequest Request) CreateConsumption() => CreateOperation(0, consuming: true);

	private (Guid OperationId, ICredentialsRequest Request) CreateOperation(long amount, bool consuming)
	{
		lock (_gate)
		{
			EnsureUsable();
			if (_validation is not null) throw new InvalidOperationException("A credential operation is already pending.");
			if (amount < 0 || amount > _maxAmount) throw new ArgumentOutOfRangeException(nameof(amount));
			ICredentialsRequest request;
			CredentialsResponseValidation validation;
			if (_credentials.Length == 0)
			{
				if (consuming || amount != 0) throw new InvalidOperationException("Explicit zero bootstrap is required first.");
				var zero = _client.CreateRequestForZeroAmount();
				(request, validation) = (zero.CredentialsRequest, zero.CredentialsResponseValidation);
			}
			else
			{
				// Request two zero credentials for consumption: shipped HandleResponse cannot validate an empty issuance.
				var real = _client.CreateRequest(new[] { amount }, _credentials.ToImmutableArray(), CancellationToken.None);
				(request, validation) = (real.CredentialsRequest, real.CredentialsResponseValidation);
			}
			_presented = validation.Presented.Select(Snapshot).ToImmutableArray();
			_requested = SnapshotRequested(validation);
			_validation = validation;
			_consuming = consuming;
			_operationId = Guid.NewGuid();
			return (_operationId, request);
		}
	}

	public Guid AcceptResponse(Guid operationId, CredentialsResponse response)
	{
		lock (_gate)
		{
			EnsurePending(operationId);
			if (_consumptionResult is not null) throw new InvalidOperationException("The response was already validated.");
			ArgumentNullException.ThrowIfNull(response);
			try
			{
				if (!_presented.SequenceEqual(_validation!.Presented.Select(Snapshot)) || !_requested.SequenceEqual(SnapshotRequested(_validation)))
					throw new InvalidOperationException("Pending validation inputs changed.");
				var issued = _client.HandleResponse(response, _validation).ToArray();
				var result = Guid.NewGuid();
				if (_consuming)
				{
					if (issued.Any(credential => credential.Value != 0)) throw new InvalidOperationException("Consumption returned nonzero credentials.");
					_consumptionResult = result;
				}
				else
				{
					_credentials = issued;
					_validation = null;
				}
				return result;
			}
			catch
			{
				// The package transcript is private and mutable. Never retry validation or reuse these credentials.
				_terminal = true;
				throw;
			}
		}
	}

	public void CommitConsumption(Guid operationId, Guid resultId)
	{
		lock (_gate)
		{
			EnsurePending(operationId);
			if (!_consuming || _consumptionResult != resultId) throw new InvalidOperationException("Consumption requires its validated result.");
			_credentials = [];
			_validation = null;
			_consumptionResult = null;
		}
	}

	private void EnsurePending(Guid operationId)
	{
		EnsureUsable();
		if (_validation is null || operationId != _operationId) throw new InvalidOperationException("No matching credential operation is pending.");
	}

	private void EnsureUsable()
	{
		if (_terminal) throw new InvalidOperationException("The client is terminal after response validation failure.");
	}

	private static LiquidCredentialSnapshot Snapshot(Credential credential) => new(credential.Value,
		Convert.ToHexString(credential.Randomness.ToBytes()), Convert.ToHexString(credential.Mac.T.ToBytes()), Convert.ToHexString(credential.Mac.V.ToBytes()));

	private static ImmutableArray<(long Value, string Randomness, string Ma)> SnapshotRequested(CredentialsResponseValidation validation) =>
		validation.Requested.Select(item => (item.Value, Convert.ToHexString(item.Randomness.ToBytes()), Convert.ToHexString(item.Ma.ToBytes()))).ToImmutableArray();
}
