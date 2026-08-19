using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;
using WalletWasabi.Liquid.Transactions;
using WalletWasabi.Liquid.Wallet;
using WalletWasabi.Liquid.Wallet.Wire;
using LiquidOrdinaryWalletPlanEncodedFrame = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanEncodedFrame;
using LiquidOrdinaryWalletPlanFundingBatch = WalletWasabi.Liquid.Wallet.Wire.LiquidOrdinaryWalletPlanEncoder.LiquidOrdinaryWalletPlanFundingBatch;

namespace WalletWasabi.Liquid.Rpc;

public sealed class ElementsRpcClient : IDisposable
{
	private const int MaxRpcResponseBytes = 1024 * 1024;
	private const int MaxJsonDepth = 64;
	private const int MaxJsonTokens = 65536;
	private const int MaxJsonStringBytes = 65536;
	private const int MaxJsonPropertyNameBytes = 256;
	private const int MaxJsonArrayItems = 4096;
	private const int MaxJsonObjectProperties = 4096;
	private const int MaxJsonNumberBytes = 128;
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private readonly HttpClient _httpClient;
	private readonly Uri _endpoint;
	private readonly ElementsRpcTimeouts _timeouts;
	private readonly bool _ownsHttpClient;
	private readonly SemaphoreSlim _probeLock = new(1, 1);
	private long _requestSequence;

	private ElementsRpcClient(
		HttpClient httpClient,
		Uri endpoint,
		ElementsRpcTimeouts timeouts,
		bool ownsHttpClient)
	{
		_httpClient = httpClient;
		_endpoint = endpoint;
		_timeouts = timeouts;
		_ownsHttpClient = ownsHttpClient;
	}

	internal ElementsRpcClient(HttpClient httpClient, ElementsRpcTimeouts? timeouts = null)
	{
		ArgumentNullException.ThrowIfNull(httpClient);
		_endpoint = RequireEndpoint(httpClient);
		_timeouts = timeouts is null ? SnapshotTimeouts(httpClient) : timeouts.Validate();
		_httpClient = httpClient;
	}

	public static ElementsRpcClient Create(
		Uri endpoint,
		ICredentials credentials,
		ElementsRpcTimeouts? timeouts = null)
	{
		ArgumentNullException.ThrowIfNull(endpoint);
		ArgumentNullException.ThrowIfNull(credentials);
		ValidateEndpoint(endpoint, nameof(endpoint));
		ElementsRpcTimeouts validatedTimeouts = (timeouts ?? ElementsRpcTimeouts.Default).Validate();
		HttpClient httpClient = CreateHttpClient(endpoint, credentials, validatedTimeouts);

		return new ElementsRpcClient(httpClient, endpoint, validatedTimeouts, ownsHttpClient: true);
	}

	private static HttpClient CreateHttpClient(
		Uri endpoint,
		ICredentials credentials,
		ElementsRpcTimeouts timeouts)
	{
#pragma warning disable CA2000 // Ownership transfers to HttpClient when construction succeeds.
		var handler = CreateTransportHandler(credentials, timeouts);
#pragma warning restore CA2000
		try
		{
			return new HttpClient(handler, disposeHandler: true)
			{
				BaseAddress = endpoint,
				Timeout = timeouts.TotalRequestTimeout,
				DefaultRequestVersion = HttpVersion.Version11,
				DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
			};
		}
		catch
		{
			handler.Dispose();
			throw;
		}
	}

	internal static SocketsHttpHandler CreateTransportHandler(
		ICredentials credentials,
		ElementsRpcTimeouts timeouts)
	{
		ArgumentNullException.ThrowIfNull(credentials);
		ArgumentNullException.ThrowIfNull(timeouts);
		ElementsRpcTimeouts validatedTimeouts = timeouts.Validate();
		return new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			AutomaticDecompression = DecompressionMethods.None,
			ConnectTimeout = validatedTimeouts.ConnectTimeout,
			Credentials = credentials,
			MaxConnectionsPerServer = 1,
			MaxResponseHeadersLength = 16,
			PreAuthenticate = true,
			UseCookies = false,
			UseProxy = false,
		};
	}

	public async Task<ElementsNodeStatus> GetNodeStatusAsync(CancellationToken cancellationToken)
	{
		await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await GetNodeStatusCoreAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_probeLock.Release();
		}
	}

	public Task<ElementsManifestBoundObservation> GetPublicNetworkObservationAsync(
		ElementsPublicNetworkManifest manifest,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		return GetPublicNetworkObservationCoreAsync(manifest, cancellationToken);
	}

	public async Task<ElementsFeeAssetGenerationObservation> GetFeeAssetGenerationObservationAsync(
		CancellationToken cancellationToken)
	{
		await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await GetFeeAssetGenerationObservationCoreAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_probeLock.Release();
		}
	}

	private async Task<ElementsManifestBoundObservation> GetPublicNetworkObservationCoreAsync(
		ElementsPublicNetworkManifest manifest,
		CancellationToken cancellationToken)
	{
		ElementsNodeStatus nodeStatus = await GetNodeStatusAsync(cancellationToken).ConfigureAwait(false);
		return manifest.BindNodeObservation(nodeStatus);
	}

	private async Task<ElementsFeeAssetGenerationObservation> GetFeeAssetGenerationObservationCoreAsync(
		CancellationToken cancellationToken)
	{
		ElementsNodeGenerationObservation generationBefore =
			await GetNodeGenerationObservationCoreAsync(cancellationToken).ConfigureAwait(false);
		JsonElement sidechain = await CallObjectAsync("getsidechaininfo", [], cancellationToken).ConfigureAwait(false);
		LiquidAssetId peggedAsset = RequiredAssetId(sidechain, "pegged_asset");
		LiquidAssetId effectiveFeeAsset = RequiredAssetId(sidechain, "fee_asset");
		ElementsNodeGenerationObservation generationAfter =
			await GetNodeGenerationObservationCoreAsync(cancellationToken).ConfigureAwait(false);

		EnsureConsistentGenerationFence(generationBefore, generationAfter);
		return new ElementsFeeAssetGenerationObservation(
			peggedAsset,
			effectiveFeeAsset,
			generationBefore,
			generationAfter);
	}

	private async Task<ElementsNodeGenerationObservation> GetNodeGenerationObservationCoreAsync(CancellationToken cancellationToken)
	{
		JsonElement generation = await CallObjectAsync("getnodegeneration", [], cancellationToken).ConfigureAwait(false);
		RequireExactObjectProperties(
			generation,
			"getnodegeneration",
			["startup_id", "chainstate_revision", "blocks", "bestblockhash"]);

		string startupId = RequiredHex32(generation, "startup_id");
		ulong chainstateRevision = RequiredCanonicalUInt64(generation, "chainstate_revision");
		int blocks = RequiredCanonicalNonNegativeInt32(generation, "blocks");
		string bestBlockHash = RequiredHex32(generation, "bestblockhash");
		return new ElementsNodeGenerationObservation(startupId, chainstateRevision, blocks, bestBlockHash);
	}

	private static void EnsureConsistentGenerationFence(
		ElementsNodeGenerationObservation generationBefore,
		ElementsNodeGenerationObservation generationAfter)
	{
		if (!StringComparer.Ordinal.Equals(generationBefore.StartupId, generationAfter.StartupId))
		{
			throw InvalidResult("getnodegeneration", "startup_id changed during the fee-asset observation");
		}
		if (generationAfter.ChainstateRevision < generationBefore.ChainstateRevision)
		{
			throw InvalidResult("getnodegeneration", "chainstate_revision regressed during the fee-asset observation");
		}
		if (generationAfter.ChainstateRevision == generationBefore.ChainstateRevision
			&& (generationAfter.Blocks != generationBefore.Blocks
				|| !StringComparer.Ordinal.Equals(generationAfter.BestBlockHash, generationBefore.BestBlockHash)))
		{
			throw InvalidResult("getnodegeneration", "an unchanged revision reported an inconsistent tip");
		}
	}

	private async Task<ElementsNodeStatus> GetNodeStatusCoreAsync(CancellationToken cancellationToken)
	{
		JsonElement network = await CallObjectAsync("getnetworkinfo", [], cancellationToken).ConfigureAwait(false);
		int version = RequiredPositiveInt32(network, "version");
		int protocolVersion = RequiredPositiveInt32(network, "protocolversion");
		string subversion = RequiredText(network, "subversion");
		bool localRelay = RequiredBoolean(network, "localrelay");
		bool networkActive = RequiredBoolean(network, "networkactive");
		bool networkWarningsPresent = RequiredWarningsPresent(network, "warnings");

		JsonElement blockchain = await CallObjectAsync("getblockchaininfo", [], cancellationToken).ConfigureAwait(false);
		string chain = RequiredChain(blockchain, "chain");
		int blocks = RequiredNonNegativeInt32(blockchain, "blocks");
		int headers = RequiredNonNegativeInt32(blockchain, "headers");
		if (headers < blocks)
		{
			throw InvalidResult("getblockchaininfo", "headers cannot be lower than blocks");
		}
		string bestBlockHash = RequiredHex32(blockchain, "bestblockhash");
		bool initialBlockDownload = RequiredBoolean(blockchain, "initialblockdownload");
		bool pruned = RequiredBoolean(blockchain, "pruned");
		bool trimHeaders = RequiredBoolean(blockchain, "trim_headers");
		bool blockchainWarningsPresent = RequiredWarningsPresent(blockchain, "warnings");

		string resolvedTipHash = await CallHex32Async("getblockhash", [blocks], cancellationToken).ConfigureAwait(false);
		if (!StringComparer.Ordinal.Equals(resolvedTipHash, bestBlockHash))
		{
			throw InvalidResult("getblockhash", "the resolved tip does not match bestblockhash");
		}
		string genesisBlockHash = blocks == 0
			? resolvedTipHash
			: await CallHex32Async("getblockhash", [0], cancellationToken).ConfigureAwait(false);

		JsonElement sidechain = await CallObjectAsync("getsidechaininfo", [], cancellationToken).ConfigureAwait(false);
		string fedpegScript = RequiredHex(sidechain, "fedpegscript");
		string peggedAsset = RequiredAssetId(sidechain, "pegged_asset").CanonicalRpcHex;
		string parentGenesisBlockHash = RequiredHex32(sidechain, "parent_blockhash", allowZero: true);
		int peginConfirmationDepth = RequiredNonNegativeInt32(sidechain, "pegin_confirmation_depth");
		bool enforcePak = RequiredBoolean(sidechain, "enforce_pak");

		return new ElementsNodeStatus(
			Chain: chain,
			Blocks: blocks,
			Headers: headers,
			BestBlockHash: bestBlockHash,
			GenesisBlockHash: genesisBlockHash,
			InitialBlockDownload: initialBlockDownload,
			Pruned: pruned,
			TrimHeaders: trimHeaders,
			BlockchainWarningsPresent: blockchainWarningsPresent,
			NetworkActive: networkActive,
			LocalRelay: localRelay,
			NetworkWarningsPresent: networkWarningsPresent,
			FedpegScript: fedpegScript,
			PeggedAsset: peggedAsset,
			ParentGenesisBlockHash: parentGenesisBlockHash,
			PeginConfirmationDepth: peginConfirmationDepth,
			EnforcePak: enforcePak,
			Version: version,
			ProtocolVersion: protocolVersion,
			Subversion: subversion);
	}

	private async Task<JsonElement> CallObjectAsync(string method, object[] parameters, CancellationToken cancellationToken)
	{
		JsonElement result = await CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
		RequireObject(result, method);
		return result;
	}

	private async Task<string> CallHex32Async(string method, object[] parameters, CancellationToken cancellationToken)
	{
		JsonElement result = await CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
		if (result.ValueKind != JsonValueKind.String || result.GetString() is not { } text)
		{
			throw InvalidResult(method, "a string result is required");
		}

		try
		{
			return ElementsNodeStatus.RequireHex32(text, method);
		}
		catch (ArgumentException)
		{
			throw InvalidResult(method, "a canonical nonzero lowercase 32-byte hash is required");
		}
	}

	private async Task<JsonElement> CallAsync(string method, object[] parameters, CancellationToken cancellationToken)
	{
		using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		totalTimeout.CancelAfter(_timeouts.TotalRequestTimeout);
		try
		{
			return await CallCoreAsync(
				method,
				parameters,
				MaxRpcResponseBytes,
				MaxJsonStringBytes,
				"the one-megabyte limit",
				totalTimeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new ElementsRpcException(
				ElementsRpcFailureKind.Timeout,
				$"Elements RPC '{method}' exceeded the total request timeout.");
		}
	}

	private async Task<JsonElement> CallCoreAsync(
		string method,
		object[] parameters,
		int maximumResponseBytes,
		int maximumJsonStringBytes,
		string responseLimitDescription,
		CancellationToken cancellationToken)
	{
		string requestId = Interlocked.Increment(ref _requestSequence).ToString(CultureInfo.InvariantCulture);
		string body = JsonSerializer.Serialize(new RpcRequest("1.0", requestId, method, parameters), SerializerOptions);
		using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json"),
		};

		using HttpResponseMessage response = await SendAsync(method, request, cancellationToken).ConfigureAwait(false);
		HttpStatusCode statusCode = response.StatusCode;
		if (response.RequestMessage?.RequestUri is not { } responseUri
			|| !Uri.Equals(responseUri.IsAbsoluteUri ? responseUri : new Uri(_endpoint, responseUri), _endpoint))
		{
			throw ProtocolFailure(method, "the configured endpoint changed", statusCode);
		}
		if (!response.IsSuccessStatusCode && !CanContainRpcError(statusCode))
		{
			throw HttpFailure(method, statusCode);
		}

		bool hasJsonContentType = StringComparer.OrdinalIgnoreCase.Equals(
			response.Content.Headers.ContentType?.MediaType,
			"application/json");
		if (!hasJsonContentType)
		{
			if (!response.IsSuccessStatusCode)
			{
				throw HttpFailure(method, statusCode);
			}
			throw ProtocolFailure(method, "an invalid content type was returned", statusCode);
		}

		byte[] responseBytes = await ReadBoundedAsync(
			method,
			response.Content,
			maximumResponseBytes,
			responseLimitDescription,
			_timeouts.ResponseIdleTimeout,
			cancellationToken).ConfigureAwait(false);
		try
		{
			ValidateJsonResourceLimits(responseBytes, method, maximumJsonStringBytes);
			using JsonDocument document = JsonDocument.Parse(responseBytes, new JsonDocumentOptions
			{
				AllowTrailingCommas = false,
				CommentHandling = JsonCommentHandling.Disallow,
				MaxDepth = MaxJsonDepth,
			});
			JsonElement root = document.RootElement;
			ValidateNoDuplicateProperties(root, method);
			RequireObject(root, method);
			RequireExactEnvelope(root, method);

			JsonElement id = RequiredProperty(root, "id", method);
			if (id.ValueKind != JsonValueKind.String || !StringComparer.Ordinal.Equals(id.GetString(), requestId))
			{
				throw InvalidResult(method, "response id does not match the request", statusCode);
			}

			JsonElement result = RequiredProperty(root, "result", method);
			JsonElement error = RequiredProperty(root, "error", method);
			if (error.ValueKind != JsonValueKind.Null)
			{
				int code = RequiredRpcErrorCode(method, result, error, statusCode);
				throw new ElementsRpcException(
					ElementsRpcFailureKind.Rpc,
					$"Elements RPC '{method}' failed with code {code}.",
					rpcCode: code,
					httpStatusCode: statusCode);
			}
			if (!response.IsSuccessStatusCode)
			{
				throw HttpFailure(method, statusCode);
			}

			return result.Clone();
		}
		catch (JsonException exception)
		{
			throw new ElementsRpcException(
				ElementsRpcFailureKind.Protocol,
				$"Elements RPC '{method}' returned invalid JSON.",
				httpStatusCode: statusCode,
				innerException: exception);
		}
	}

	private async Task<HttpResponseMessage> SendAsync(
		string method,
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		try
		{
			return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException exception)
		{
			throw new ElementsRpcException(
				ElementsRpcFailureKind.Transport,
				$"Elements RPC '{method}' transport failed.",
				innerException: exception);
		}
	}

	private static async Task<byte[]> ReadBoundedAsync(
		string method,
		HttpContent content,
		int maximumResponseBytes,
		string responseLimitDescription,
		TimeSpan responseIdleTimeout,
		CancellationToken cancellationToken)
	{
		if (content.Headers.ContentLength is > 0 and var contentLength
			&& contentLength > maximumResponseBytes)
		{
			throw ProtocolFailure(method, $"the response exceeded {responseLimitDescription}");
		}

		using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using var output = new MemoryStream();
		byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
		try
		{
			while (true)
			{
				using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				idleTimeout.CancelAfter(responseIdleTimeout);
				int read;
				try
				{
					read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), idleTimeout.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					throw new ElementsRpcException(
						ElementsRpcFailureKind.Timeout,
						$"Elements RPC '{method}' response body exceeded the idle timeout.");
				}

				if (read == 0)
				{
					return output.ToArray();
				}
				if (output.Length + read > maximumResponseBytes)
				{
					throw ProtocolFailure(method, $"the response exceeded {responseLimitDescription}");
				}

				output.Write(buffer, 0, read);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
		}
	}

	private static void ValidateJsonResourceLimits(
		ReadOnlySpan<byte> responseBytes,
		string method,
		int maximumJsonStringBytes)
	{
		var reader = new Utf8JsonReader(responseBytes, new JsonReaderOptions
		{
			AllowTrailingCommas = false,
			CommentHandling = JsonCommentHandling.Disallow,
			MaxDepth = MaxJsonDepth,
		});
		Span<JsonContainerFrame> containers = stackalloc JsonContainerFrame[MaxJsonDepth];
		int containerCount = 0;
		int tokenCount = 0;

		while (reader.Read())
		{
			tokenCount++;
			if (tokenCount > MaxJsonTokens)
			{
				throw ProtocolFailure(method, "the JSON token limit was exceeded");
			}

			switch (reader.TokenType)
			{
				case JsonTokenType.PropertyName:
					RequireValueLength(reader, MaxJsonPropertyNameBytes, method, "JSON property name");
					if (containerCount == 0 || containers[containerCount - 1].IsArray)
					{
						throw ProtocolFailure(method, "a JSON property appeared outside an object");
					}
					containers[containerCount - 1].Count++;
					if (containers[containerCount - 1].Count > MaxJsonObjectProperties)
					{
						throw ProtocolFailure(method, "the JSON object-property limit was exceeded");
					}
					break;
				case JsonTokenType.StartObject:
				case JsonTokenType.StartArray:
					CountArrayValue(containers, containerCount, method);
					if (containerCount == containers.Length)
					{
						throw ProtocolFailure(method, "the JSON depth limit was exceeded");
					}
					containers[containerCount++] = new JsonContainerFrame(reader.TokenType == JsonTokenType.StartArray);
					break;
				case JsonTokenType.EndObject:
				case JsonTokenType.EndArray:
					if (containerCount == 0)
					{
						throw ProtocolFailure(method, "the JSON container structure is invalid");
					}
					containerCount--;
					break;
				case JsonTokenType.String:
					RequireValueLength(reader, maximumJsonStringBytes, method, "JSON string");
					CountArrayValue(containers, containerCount, method);
					break;
				case JsonTokenType.Number:
					RequireValueLength(reader, MaxJsonNumberBytes, method, "JSON number");
					CountArrayValue(containers, containerCount, method);
					break;
				case JsonTokenType.True:
				case JsonTokenType.False:
				case JsonTokenType.Null:
					CountArrayValue(containers, containerCount, method);
					break;
			}
		}
	}

	private static void CountArrayValue(
		Span<JsonContainerFrame> containers,
		int containerCount,
		string method)
	{
		if (containerCount > 0 && containers[containerCount - 1].IsArray)
		{
			containers[containerCount - 1].Count++;
			if (containers[containerCount - 1].Count > MaxJsonArrayItems)
			{
				throw ProtocolFailure(method, "the JSON array-item limit was exceeded");
			}
		}
	}

	private static void RequireValueLength(
		Utf8JsonReader reader,
		int maximumBytes,
		string method,
		string valueName)
	{
		long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
		if (length > maximumBytes)
		{
			throw ProtocolFailure(method, $"the {valueName} limit was exceeded");
		}
	}

	private static Uri RequireEndpoint(HttpClient httpClient)
	{
		if (httpClient.BaseAddress is not { IsAbsoluteUri: true } endpoint)
		{
			throw new ArgumentException("An absolute Elements RPC base address is required.", nameof(httpClient));
		}
		ValidateEndpoint(endpoint, nameof(httpClient));
		return endpoint;
	}

	private static ElementsRpcTimeouts SnapshotTimeouts(HttpClient httpClient)
	{
		TimeSpan totalTimeout = httpClient.Timeout;
		if (totalTimeout == Timeout.InfiniteTimeSpan || totalTimeout <= TimeSpan.Zero || totalTimeout > TimeSpan.FromMinutes(10))
		{
			throw new ArgumentException("A finite positive Elements RPC timeout no longer than ten minutes is required.", nameof(httpClient));
		}

		return new ElementsRpcTimeouts(
			ConnectTimeout: Min(totalTimeout, ElementsRpcTimeouts.Default.ConnectTimeout),
			TotalRequestTimeout: totalTimeout,
			ResponseIdleTimeout: Min(totalTimeout, ElementsRpcTimeouts.Default.ResponseIdleTimeout)).Validate();
	}

	private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

	private static void ValidateEndpoint(Uri endpoint, string parameterName)
	{
		bool isHttp = StringComparer.OrdinalIgnoreCase.Equals(endpoint.Scheme, Uri.UriSchemeHttp);
		bool isHttps = StringComparer.OrdinalIgnoreCase.Equals(endpoint.Scheme, Uri.UriSchemeHttps);
		if ((!isHttp && !isHttps)
			|| (isHttp && !StringComparer.Ordinal.Equals(endpoint.Host, IPAddress.Loopback.ToString())))
		{
			throw new ArgumentException("Elements RPC requires HTTPS or exact plaintext loopback 127.0.0.1.", parameterName);
		}
		if (!string.IsNullOrEmpty(endpoint.UserInfo)
			|| !string.IsNullOrEmpty(endpoint.Query)
			|| !string.IsNullOrEmpty(endpoint.Fragment)
			|| !StringComparer.Ordinal.Equals(endpoint.AbsolutePath, "/"))
		{
			throw new ArgumentException("Elements RPC credentials and routing data must not appear in the endpoint URI.", parameterName);
		}
	}

	private static bool CanContainRpcError(HttpStatusCode statusCode) =>
		statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.InternalServerError;

	private static bool IsExpectedRpcStatus(int rpcCode, HttpStatusCode statusCode) => rpcCode switch
	{
		-32600 => statusCode == HttpStatusCode.BadRequest,
		-32601 => statusCode == HttpStatusCode.NotFound,
		_ => statusCode == HttpStatusCode.InternalServerError,
	};

	private static void ValidateNoDuplicateProperties(JsonElement value, string method)
	{
		switch (value.ValueKind)
		{
			case JsonValueKind.Object:
				var names = new HashSet<string>(StringComparer.Ordinal);
				foreach (JsonProperty property in value.EnumerateObject())
				{
					if (!names.Add(property.Name))
					{
						throw InvalidResult(method, "response contains a duplicate JSON field");
					}
					ValidateNoDuplicateProperties(property.Value, method);
				}
				break;
			case JsonValueKind.Array:
				foreach (JsonElement item in value.EnumerateArray())
				{
					ValidateNoDuplicateProperties(item, method);
				}
				break;
		}
	}

	private static void RequireExactEnvelope(JsonElement root, string method)
	{
		int resultCount = 0;
		int errorCount = 0;
		int idCount = 0;
		int total = 0;
		foreach (JsonProperty property in root.EnumerateObject())
		{
			total++;
			switch (property.Name)
			{
				case "result":
					resultCount++;
					break;
				case "error":
					errorCount++;
					break;
				case "id":
					idCount++;
					break;
				default:
					throw InvalidResult(method, "response envelope contains an unknown field");
			}
		}

		if (total != 3 || resultCount != 1 || errorCount != 1 || idCount != 1)
		{
			throw InvalidResult(method, "response envelope is incomplete or duplicated");
		}
	}

	private static JsonElement RequiredProperty(JsonElement value, string propertyName, string method)
	{
		int count = 0;
		JsonElement result = default;
		foreach (JsonProperty property in value.EnumerateObject())
		{
			if (StringComparer.Ordinal.Equals(property.Name, propertyName))
			{
				count++;
				result = property.Value;
			}
		}

		if (count != 1)
		{
			throw InvalidResult(method, $"field '{propertyName}' is missing or duplicated");
		}

		return result;
	}

	private static string RequiredString(JsonElement value, string propertyName)
	{
		JsonElement property = RequiredProperty(value, propertyName, "node identity");
		if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } result)
		{
			throw InvalidResult("node identity", $"field '{propertyName}' must be a string");
		}

		return result;
	}

	private static string RequiredText(JsonElement value, string propertyName)
	{
		string result = RequiredString(value, propertyName);
		try
		{
			return ElementsNodeStatus.RequireText(result, propertyName);
		}
		catch (ArgumentException)
		{
			throw InvalidResult("node identity", $"field '{propertyName}' must be bounded nonempty text without control characters");
		}
	}

	private static string RequiredChain(JsonElement value, string propertyName)
	{
		string result = RequiredString(value, propertyName);
		try
		{
			return ElementsNodeStatus.RequireChain(result, propertyName);
		}
		catch (ArgumentException)
		{
			throw InvalidResult("node identity", $"field '{propertyName}' must be a canonical chain name");
		}
	}

	private static string RequiredHex32(JsonElement value, string propertyName, bool allowZero = false)
	{
		string result = RequiredString(value, propertyName);
		try
		{
			return allowZero
				? ElementsNodeStatus.RequireHex32AllowZero(result, propertyName)
				: ElementsNodeStatus.RequireHex32(result, propertyName);
		}
		catch (ArgumentException)
		{
			string nonzero = allowZero ? "" : " nonzero";
			throw InvalidResult("node identity", $"field '{propertyName}' must be canonical{nonzero} lowercase hexadecimal");
		}
	}

	private static string RequiredHex(JsonElement value, string propertyName)
	{
		string result = RequiredString(value, propertyName);
		try
		{
			return ElementsNodeStatus.RequireHex(result, propertyName);
		}
		catch (ArgumentException)
		{
			throw InvalidResult("node identity", $"field '{propertyName}' must be nonempty canonical lowercase hexadecimal");
		}
	}

	private static LiquidAssetId RequiredAssetId(JsonElement value, string propertyName)
	{
		string result = RequiredString(value, propertyName);
		try
		{
			return LiquidAssetId.ParseRpcHex(result, propertyName);
		}
		catch (ArgumentException)
		{
			throw InvalidResult("node identity", $"field '{propertyName}' must be a canonical nonzero Liquid asset identifier");
		}
	}

	private static int RequiredNonNegativeInt32(JsonElement value, string propertyName)
	{
		JsonElement property = RequiredProperty(value, propertyName, "node identity");
		if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int result) || result < 0)
		{
			throw InvalidResult("node identity", $"field '{propertyName}' must be a non-negative 32-bit integer");
		}

		return result;
	}

	private static int RequiredPositiveInt32(JsonElement value, string propertyName)
	{
		int result = RequiredNonNegativeInt32(value, propertyName);
		if (result == 0)
		{
			throw InvalidResult("node identity", $"field '{propertyName}' must be a positive 32-bit integer");
		}

		return result;
	}

	private static int RequiredCanonicalNonNegativeInt32(JsonElement value, string propertyName)
	{
		JsonElement property = RequiredProperty(value, propertyName, "node generation");
		string raw = property.GetRawText();
		if (!IsCanonicalUnsignedInteger(raw)
			|| property.ValueKind != JsonValueKind.Number
			|| !property.TryGetInt32(out int result)
			|| result < 0)
		{
			throw InvalidResult("node generation", $"field '{propertyName}' must be a canonical non-negative 32-bit integer");
		}

		return result;
	}

	private static ulong RequiredCanonicalUInt64(JsonElement value, string propertyName)
	{
		JsonElement property = RequiredProperty(value, propertyName, "node generation");
		string raw = property.GetRawText();
		if (!IsCanonicalUnsignedInteger(raw)
			|| property.ValueKind != JsonValueKind.Number
			|| !property.TryGetUInt64(out ulong result))
		{
			throw InvalidResult("node generation", $"field '{propertyName}' must be a canonical unsigned 64-bit integer");
		}

		return result;
	}

	private static bool IsCanonicalUnsignedInteger(string raw)
	{
		if (raw.Length == 0 || (raw.Length > 1 && raw[0] == '0'))
		{
			return false;
		}

		foreach (char character in raw)
		{
			if (!char.IsAsciiDigit(character))
			{
				return false;
			}
		}

		return true;
	}

	private static bool RequiredBoolean(JsonElement value, string propertyName)
	{
		JsonElement property = RequiredProperty(value, propertyName, "node identity");
		return property.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => throw InvalidResult("node identity", $"field '{propertyName}' must be a boolean"),
		};
	}

	private static bool RequiredWarningsPresent(JsonElement value, string propertyName)
	{
		JsonElement property = RequiredProperty(value, propertyName, "node status");
		if (property.ValueKind == JsonValueKind.String
			&& property.GetString() is { } legacyWarnings
			&& legacyWarnings.Length <= 4096)
		{
			return legacyWarnings.Length > 0;
		}

		if (property.ValueKind == JsonValueKind.Array)
		{
			int entryCount = 0;
			int totalCharacters = 0;
			foreach (JsonElement entry in property.EnumerateArray())
			{
				entryCount++;
				if (entryCount > 64
					|| entry.ValueKind != JsonValueKind.String
					|| entry.GetString() is not { Length: > 0 } warning
					|| warning.Length > 4096 - totalCharacters)
				{
					throw InvalidResult("node status", $"field '{propertyName}' must be a bounded string or string array");
				}

				totalCharacters += warning.Length;
			}

			return entryCount > 0;
		}

		throw InvalidResult("node status", $"field '{propertyName}' must be a bounded string or string array");
	}

	private static void RequireObject(JsonElement value, string method)
	{
		if (value.ValueKind != JsonValueKind.Object)
		{
			throw InvalidResult(method, "an object result is required");
		}
	}

	private static void RequireExactObjectProperties(
		JsonElement value,
		string method,
		IReadOnlyCollection<string> expectedProperties)
	{
		var remaining = new HashSet<string>(expectedProperties, StringComparer.Ordinal);
		int total = 0;
		foreach (JsonProperty property in value.EnumerateObject())
		{
			total++;
			if (!remaining.Remove(property.Name))
			{
				throw InvalidResult(method, "the result contains an unknown or duplicated field");
			}
		}

		if (total != expectedProperties.Count || remaining.Count != 0)
		{
			throw InvalidResult(method, "the result does not match the required field set");
		}
	}

	private static int RequiredRpcErrorCode(
		string method,
		JsonElement result,
		JsonElement error,
		HttpStatusCode statusCode)
	{
		if (result.ValueKind != JsonValueKind.Null || error.ValueKind != JsonValueKind.Object)
		{
			throw InvalidResult(method, "an RPC error requires a null result and an object error", statusCode);
		}

		int propertyCount = 0;
		foreach (JsonProperty property in error.EnumerateObject())
		{
			propertyCount++;
			if (!property.NameEquals("code") && !property.NameEquals("message"))
			{
				throw InvalidResult(method, "the RPC error object contains an unknown field", statusCode);
			}
		}
		if (propertyCount != 2)
		{
			throw InvalidResult(method, "the RPC error object must contain exactly code and message", statusCode);
		}

		JsonElement codeProperty = RequiredProperty(error, "code", method);
		JsonElement messageProperty = RequiredProperty(error, "message", method);
		if (codeProperty.ValueKind != JsonValueKind.Number || !codeProperty.TryGetInt32(out int code))
		{
			throw InvalidResult(method, "the RPC error code must be a 32-bit integer", statusCode);
		}
		if (messageProperty.ValueKind != JsonValueKind.String
			|| messageProperty.GetString() is not { } message
			|| message.Length > 4096)
		{
			throw InvalidResult(method, "the RPC error message must be a bounded string", statusCode);
		}
		if (!IsExpectedRpcStatus(code, statusCode))
		{
			throw InvalidResult(method, "the HTTP status does not match the RPC error code", statusCode);
		}

		return code;
	}

	private static ElementsRpcException InvalidResult(
		string method,
		string reason,
		HttpStatusCode? httpStatusCode = null) =>
		new(
			ElementsRpcFailureKind.Protocol,
			$"Elements RPC '{method}' returned an invalid result: {reason}.",
			httpStatusCode: httpStatusCode);

	private static ElementsRpcException ProtocolFailure(
		string method,
		string reason,
		HttpStatusCode? httpStatusCode = null) =>
		new(
			ElementsRpcFailureKind.Protocol,
			$"Elements RPC '{method}' protocol failure: {reason}.",
			httpStatusCode: httpStatusCode);

	private static ElementsRpcException HttpFailure(string method, HttpStatusCode statusCode) =>
		new(
			ElementsRpcFailureKind.Http,
			$"Elements RPC '{method}' returned HTTP {(int)statusCode}.",
			httpStatusCode: statusCode);

	public async Task<ElementsExpectationBoundNodeObservation> GetExpectationBoundNodeObservationAsync(
		ElementsNodeExpectation expectation,
		string expectedEffectiveFeeAsset,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(expectation);
		ElementsNodeExpectation normalizedExpectation = expectation.Normalize();
		LiquidAssetId normalizedEffectiveFeeAsset =
			LiquidAssetId.ParseRpcHex(expectedEffectiveFeeAsset, nameof(expectedEffectiveFeeAsset));

		await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await GetExpectationBoundNodeObservationCoreAsync(
				normalizedExpectation,
				normalizedEffectiveFeeAsset,
				cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_probeLock.Release();
		}
	}

	/// <summary>
	/// Submits one canonical signed transaction while holding the node-probe lock across the exact
	/// expectation, effective-fee-asset, and generation fence. A successful receipt records node
	/// acceptance only; it is not confirmation, currentness, propagation, or transaction-id authority.
	/// No retry or fallback is performed.
	/// </summary>
	public async Task<ElementsExpectationBoundBroadcastReceipt> BroadcastExpectationBoundRawTransactionAsync(
		ElementsNodeExpectation expectation,
		string expectedEffectiveFeeAsset,
		string signedTransactionHex,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(expectation);
		ElementsNodeExpectation normalizedExpectation = expectation.Normalize();
		LiquidAssetId normalizedEffectiveFeeAsset =
			LiquidAssetId.ParseRpcHex(expectedEffectiveFeeAsset, nameof(expectedEffectiveFeeAsset));
		RequireCanonicalTransactionHex(signedTransactionHex, nameof(signedTransactionHex));

		await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ElementsExpectationBoundNodeObservation nodeObservation =
				await GetExpectationBoundNodeObservationCoreAsync(
					normalizedExpectation,
					normalizedEffectiveFeeAsset,
					cancellationToken).ConfigureAwait(false);
			string acceptedTransactionIdHex = await CallHex32Async(
				"sendrawtransaction",
				[signedTransactionHex],
				cancellationToken).ConfigureAwait(false);
			ElementsNodeGenerationObservation generationAfterBroadcast =
				await GetNodeGenerationObservationCoreAsync(cancellationToken).ConfigureAwait(false);
			if (generationAfterBroadcast != nodeObservation.Generation)
			{
				throw InvalidResult(
					"expectation-bound transaction broadcast",
					"node generation changed during transaction submission");
			}

			return new ElementsExpectationBoundBroadcastReceipt(
				nodeObservation,
				acceptedTransactionIdHex);
		}
		finally
		{
			_probeLock.Release();
		}
	}

	private static void RequireCanonicalTransactionHex(string value, string parameterName)
	{
		ArgumentNullException.ThrowIfNull(value, parameterName);
		if (value.Length == 0 || value.Length % 2 != 0)
		{
			throw new ArgumentException(
				"A nonempty even-length lowercase hexadecimal transaction is required.",
				parameterName);
		}

		foreach (char character in value)
		{
			if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
			{
				throw new ArgumentException(
					"A nonempty even-length lowercase hexadecimal transaction is required.",
					parameterName);
			}
		}
	}

	private async Task<ElementsExpectationBoundNodeObservation> GetExpectationBoundNodeObservationCoreAsync(
		ElementsNodeExpectation expectation,
		LiquidAssetId expectedEffectiveFeeAsset,
		CancellationToken cancellationToken)
	{
		ElementsNodeGenerationObservation generationBeforeStatus =
			await GetNodeGenerationObservationCoreAsync(cancellationToken).ConfigureAwait(false);
		ElementsNodeStatus nodeStatus = await GetNodeStatusCoreAsync(cancellationToken).ConfigureAwait(false);
		ElementsNodeGenerationObservation generationAfterStatus =
			await GetNodeGenerationObservationCoreAsync(cancellationToken).ConfigureAwait(false);
		ElementsFeeAssetGenerationObservation feeObservation =
			await GetFeeAssetGenerationObservationCoreAsync(cancellationToken).ConfigureAwait(false);

		EnsureExactExpectationBoundGenerationFence(
			generationBeforeStatus,
			generationAfterStatus,
			feeObservation.GenerationBefore,
			feeObservation.GenerationAfter,
			nodeStatus);
		nodeStatus.EnsureMatches(expectation);

		var mismatches = new List<string>();
		if (!StringComparer.Ordinal.Equals(nodeStatus.PeggedAsset, feeObservation.PeggedAsset))
		{
			mismatches.Add("pegged_asset");
		}
		if (!StringComparer.Ordinal.Equals(
			feeObservation.EffectiveFeeAsset,
			expectedEffectiveFeeAsset.CanonicalRpcHex))
		{
			mismatches.Add("fee_asset");
		}
		if (mismatches.Count > 0)
		{
			throw new ElementsNodeMismatchException(mismatches);
		}

		return new ElementsExpectationBoundNodeObservation(
			expectation,
			expectedEffectiveFeeAsset.CanonicalRpcHex,
			nodeStatus,
			generationBeforeStatus);
	}

	private static void EnsureExactExpectationBoundGenerationFence(
		ElementsNodeGenerationObservation generationBeforeStatus,
		ElementsNodeGenerationObservation generationAfterStatus,
		ElementsNodeGenerationObservation generationBeforeFee,
		ElementsNodeGenerationObservation generationAfterFee,
		ElementsNodeStatus nodeStatus)
	{
		if (generationBeforeStatus != generationAfterStatus
			|| generationBeforeStatus != generationBeforeFee
			|| generationBeforeStatus != generationAfterFee)
		{
			throw InvalidResult(
				"expectation-bound node observation",
				"node generation changed during the observation");
		}
		if (nodeStatus.Blocks != generationBeforeStatus.Blocks
			|| !StringComparer.Ordinal.Equals(
				nodeStatus.BestBlockHash,
				generationBeforeStatus.BestBlockHash))
		{
			throw InvalidResult(
				"expectation-bound node observation",
				"node status did not match the generation fence");
		}
	}

	/// <summary>
	/// Fetches bounded raw transaction bytes while holding the node-probe lock across the exact
	/// expectation, effective-fee-asset, and generation fence. No retry is performed.
	/// </summary>
	public async Task<ElementsExpectationBoundRawTransactionBatch> GetExpectationBoundRawTransactionsAsync(
		ElementsNodeExpectation expectation,
		string expectedEffectiveFeeAsset,
		IReadOnlyList<ElementsRawTransactionRequest> requests,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(expectation);
		ArgumentNullException.ThrowIfNull(requests);
		ElementsNodeExpectation normalizedExpectation = expectation.Normalize();
		LiquidAssetId normalizedEffectiveFeeAsset =
			LiquidAssetId.ParseRpcHex(expectedEffectiveFeeAsset, nameof(expectedEffectiveFeeAsset));
		int requestCount = requests.Count;
		if (requestCount is < 1 or > MaxRawTransactionCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(requests),
				"Between one and one hundred raw transaction requests are required.");
		}

		var normalizedRequests = new ElementsRawTransactionRequest[requestCount];
		var transactionIds = new HashSet<string>(StringComparer.Ordinal);
		for (int index = 0; index < normalizedRequests.Length; index++)
		{
			ElementsRawTransactionRequest request = requests[index]
				?? throw new ArgumentException("Every raw transaction request is required.", nameof(requests));
			ElementsRawTransactionRequest normalizedRequest = request.Normalize(nameof(requests));
			if (!transactionIds.Add(normalizedRequest.TransactionId))
			{
				throw new ArgumentException("Raw transaction request identifiers must be unique.", nameof(requests));
			}

			normalizedRequests[index] = normalizedRequest;
		}

		await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await GetExpectationBoundRawTransactionsCoreAsync(
				normalizedExpectation,
				normalizedEffectiveFeeAsset,
				normalizedRequests,
				cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_probeLock.Release();
		}
	}

	private async Task<ElementsExpectationBoundRawTransactionBatch> GetExpectationBoundRawTransactionsCoreAsync(
		ElementsNodeExpectation expectation,
		LiquidAssetId expectedEffectiveFeeAsset,
		ElementsRawTransactionRequest[] requests,
		CancellationToken cancellationToken)
	{
		ElementsExpectationBoundNodeObservation nodeObservation =
			await GetExpectationBoundNodeObservationCoreAsync(
				expectation,
				expectedEffectiveFeeAsset,
				cancellationToken).ConfigureAwait(false);
		var transactions = new ElementsRawTransactionObservation[requests.Length];
		long aggregateBytes = 0;
		for (int index = 0; index < requests.Length; index++)
		{
			byte[] transactionBytes = await GetRawTransactionBytesCoreAsync(
				requests[index],
				cancellationToken).ConfigureAwait(false);
			aggregateBytes = checked(aggregateBytes + transactionBytes.Length);
			if (aggregateBytes > MaxRawTransactionBatchBytes)
			{
				throw InvalidResult(
					"expectation-bound raw transaction batch",
					"the aggregate raw transaction byte limit was exceeded");
			}

			transactions[index] = new ElementsRawTransactionObservation(
				requests[index],
				transactionBytes);
		}

		ElementsNodeGenerationObservation generationAfterTransactions =
			await GetNodeGenerationObservationCoreAsync(cancellationToken).ConfigureAwait(false);
		if (generationAfterTransactions != nodeObservation.Generation)
		{
			throw InvalidResult(
				"expectation-bound raw transaction batch",
				"node generation changed during raw transaction acquisition");
		}

		return new ElementsExpectationBoundRawTransactionBatch(nodeObservation, transactions);
	}

	private async Task<byte[]> GetRawTransactionBytesCoreAsync(
		ElementsRawTransactionRequest request,
		CancellationToken cancellationToken)
	{
		object[] parameters = request.BlockHash is null
			? [request.TransactionId, false]
			: [request.TransactionId, false, request.BlockHash];
		JsonElement result = await CallWithResponseLimitsAsync(
			"getrawtransaction",
			parameters,
			MaxRawTransactionRpcResponseBytes,
			MaxRawTransactionHexBytes,
			"the raw-transaction response limit",
			cancellationToken).ConfigureAwait(false);
		if (result.ValueKind != JsonValueKind.String || result.GetString() is not { } text)
		{
			throw InvalidResult("getrawtransaction", "a canonical raw transaction is required");
		}

		string rawJson = result.GetRawText();
		if (text.Length is 0 or > MaxRawTransactionHexBytes
			|| text.Length % 2 != 0
			|| rawJson.Length != text.Length + 2
			|| rawJson[0] != '"'
			|| rawJson[^1] != '"'
			|| !rawJson.AsSpan(1, text.Length).SequenceEqual(text.AsSpan()))
		{
			throw InvalidResult("getrawtransaction", "a canonical raw transaction is required");
		}
		foreach (char character in text)
		{
			if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
			{
				throw InvalidResult("getrawtransaction", "a canonical raw transaction is required");
			}
		}

		return Convert.FromHexString(text);
	}

	private async Task<JsonElement> CallWithResponseLimitsAsync(
		string method,
		object[] parameters,
		int maximumResponseBytes,
		int maximumJsonStringBytes,
		string responseLimitDescription,
		CancellationToken cancellationToken)
	{
		using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		totalTimeout.CancelAfter(_timeouts.TotalRequestTimeout);
		try
		{
			return await CallCoreAsync(
				method,
				parameters,
				maximumResponseBytes,
				maximumJsonStringBytes,
				responseLimitDescription,
				totalTimeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new ElementsRpcException(
				ElementsRpcFailureKind.Timeout,
				$"Elements RPC '{method}' exceeded the total request timeout.");
		}
	}

	public void Dispose()
	{
		_probeLock.Dispose();
		if (_ownsHttpClient)
		{
			_httpClient.Dispose();
		}
	}

	/// <summary>
	/// Acquires the exact candidate and caller-declared previous transaction bytes under the existing
	/// expectation, effective-fee-asset, and generation fence, then copies them into one canonical
	/// ordinary-wallet plan frame. This operation does not validate transaction identities or block
	/// membership and grants no artifact, runtime, currentness, reservation, signing, or broadcast
	/// authority. No retry is performed.
	/// </summary>
	internal async Task<(
		ElementsExpectationBoundNodeObservation NodeObservation,
		LiquidOrdinaryWalletPlanEncodedFrame Frame)> EncodeExpectationBoundOrdinaryWalletPlanAsync(
		ElementsNodeExpectation expectation,
		string expectedEffectiveFeeAsset,
		ReadOnlyMemory<byte> sourceEpoch,
		LiquidOrdinaryWalletExactSpendPlan plan,
		IReadOnlyList<IReadOnlyList<string>?> previousTransactionIdsBySelectedInput,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(expectation);
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(previousTransactionIdsBySelectedInput);
		cancellationToken.ThrowIfCancellationRequested();
		ElementsNodeExpectation normalizedExpectation = expectation.Normalize();
		LiquidAssetId normalizedEffectiveFeeAsset =
			LiquidAssetId.ParseRpcHex(expectedEffectiveFeeAsset, nameof(expectedEffectiveFeeAsset));
		ElementsPublicNetworkManifest planManifest = GetReviewedPlanManifest(plan);
		if (!StringComparer.Ordinal.Equals(normalizedExpectation.Chain, planManifest.ChainRpcName)
			|| !StringComparer.Ordinal.Equals(normalizedExpectation.PeggedAsset, planManifest.PeggedAssetId)
			|| !StringComparer.Ordinal.Equals(
				normalizedEffectiveFeeAsset.CanonicalRpcHex,
				planManifest.RequiredFeeAssetId)
			|| normalizedEffectiveFeeAsset != plan.GetPeggedAssetId())
		{
			throw new ArgumentException(
				"The node expectation and effective fee asset must match the ordinary-wallet plan context.");
		}

		byte[] ownedSourceEpoch = CopyAndValidateSourceEpoch(sourceEpoch.Span);
		bool lockHeld = false;
		try
		{
			(
				ElementsRawTransactionRequest[] requests,
				IReadOnlyList<string>?[] normalizedPreviousTransactionIds) =
				CreateOrdinaryWalletPlanRawTransactionRequests(
					plan,
					previousTransactionIdsBySelectedInput);

			await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
			lockHeld = true;
			ElementsExpectationBoundRawTransactionBatch rawTransactions =
				await GetExpectationBoundRawTransactionsCoreAsync(
					normalizedExpectation,
					normalizedEffectiveFeeAsset,
					requests,
					cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			_ = planManifest.BindNodeObservation(rawTransactions.NodeObservation.NodeStatus);
			if (!rawTransactions.TryCreateOrdinaryWalletPlanFundingBatch(
				plan,
				normalizedPreviousTransactionIds,
				out LiquidOrdinaryWalletPlanFundingBatch? fundingBatch,
				out LiquidOrdinaryWalletPlanWireErrorCode fundingErrorCode))
			{
				fundingBatch?.Dispose();
				throw InvalidResult(
					"expectation-bound ordinary-wallet plan frame",
					fundingErrorCode.GetMessage());
			}

			using (fundingBatch)
			{
				LiquidOrdinaryWalletPlanEncodedFrame? frame = null;
				if (!LiquidOrdinaryWalletPlanEncoder.TryEncode(
					ownedSourceEpoch,
					plan,
					fundingBatch,
					out frame,
					out LiquidOrdinaryWalletPlanWireErrorCode encodingErrorCode))
				{
					frame?.Dispose();
					throw InvalidResult(
						"expectation-bound ordinary-wallet plan frame",
						encodingErrorCode.GetMessage());
				}

				return (
					rawTransactions.NodeObservation,
					frame ?? throw new InvalidOperationException(
						"Ordinary-wallet plan encoding returned no frame owner."));
			}
		}
		finally
		{
			if (lockHeld)
			{
				_probeLock.Release();
			}
			CryptographicOperations.ZeroMemory(ownedSourceEpoch);
		}
	}

	private static ElementsPublicNetworkManifest GetReviewedPlanManifest(
		LiquidOrdinaryWalletExactSpendPlan plan)
	{
		string manifestId = plan.GetDestinationNetworkManifestId();
		if (StringComparer.Ordinal.Equals(
			manifestId,
			ElementsPublicNetworkManifest.LiquidMainnet.ManifestId))
		{
			return ElementsPublicNetworkManifest.LiquidMainnet;
		}
		if (StringComparer.Ordinal.Equals(
			manifestId,
			ElementsPublicNetworkManifest.LiquidTestnet.ManifestId))
		{
			return ElementsPublicNetworkManifest.LiquidTestnet;
		}

		throw new ArgumentException(
			"The ordinary-wallet plan must use a reviewed public-network manifest.",
			nameof(plan));
	}

	private static byte[] CopyAndValidateSourceEpoch(ReadOnlySpan<byte> sourceEpoch)
	{
		if (sourceEpoch.Length != LiquidOrdinaryWalletPlanWireLimits.SourceEpochLength)
		{
			throw new ArgumentException(
				"An exact nonzero ordinary-wallet plan source epoch is required.",
				nameof(sourceEpoch));
		}

		byte[] ownedSourceEpoch = sourceEpoch.ToArray();
		byte aggregate = 0;
		for (int index = 0; index < ownedSourceEpoch.Length; index++)
		{
			aggregate |= ownedSourceEpoch[index];
		}
		if (aggregate == 0)
		{
			CryptographicOperations.ZeroMemory(ownedSourceEpoch);
			throw new ArgumentException(
				"An exact nonzero ordinary-wallet plan source epoch is required.",
				nameof(sourceEpoch));
		}

		return ownedSourceEpoch;
	}

	private static (
		ElementsRawTransactionRequest[] Requests,
		IReadOnlyList<string>?[] PreviousTransactionIdsBySelectedInput)
		CreateOrdinaryWalletPlanRawTransactionRequests(
			LiquidOrdinaryWalletExactSpendPlan plan,
			IReadOnlyList<IReadOnlyList<string>?> previousTransactionIdsBySelectedInput)
	{
		ReadOnlySpan<LiquidWalletCoinControlEntry> selectedEntries =
			plan.GetSelectedEntriesForWireEncoding();
		if (previousTransactionIdsBySelectedInput.Count != selectedEntries.Length)
		{
			throw new ArgumentException(
				"The previous-transaction mapping must contain one row per selected input.",
				nameof(previousTransactionIdsBySelectedInput));
		}

		var blockHashesByTransactionId = new Dictionary<string, string?>(StringComparer.Ordinal);
		for (int selectedIndex = 0; selectedIndex < selectedEntries.Length; selectedIndex++)
		{
			LiquidWalletCoinControlEntry selectedEntry = selectedEntries[selectedIndex];
			string transactionId = selectedEntry.OutPoint.TransactionId.CanonicalRpcHex;
			string? blockHash = selectedEntry.Confirmation?.CanonicalBlockHash;
			if (blockHashesByTransactionId.TryGetValue(transactionId, out string? priorBlockHash))
			{
				if (!StringComparer.Ordinal.Equals(priorBlockHash, blockHash))
				{
					throw new ArgumentException(
						"Selected inputs from one transaction must have one confirmation binding.",
						nameof(plan));
				}
			}
			else
			{
				blockHashesByTransactionId.Add(transactionId, blockHash);
			}
		}

		var normalizedPreviousTransactionIds =
			new IReadOnlyList<string>?[selectedEntries.Length];
		var previousIdsByCandidateId = new Dictionary<string, string[]>(StringComparer.Ordinal);
		int aggregatePreviousCount = 0;
		for (int selectedIndex = 0; selectedIndex < selectedEntries.Length; selectedIndex++)
		{
			IReadOnlyList<string>? sourcePreviousIds =
				previousTransactionIdsBySelectedInput[selectedIndex];
			if (sourcePreviousIds is null)
			{
				throw new ArgumentException(
					"Every selected input requires a previous-transaction row.",
					nameof(previousTransactionIdsBySelectedInput));
			}

			int previousCount = sourcePreviousIds.Count;
			if (previousCount < 0
				|| aggregatePreviousCount >
					LiquidOrdinaryWalletPlanWireLimits.MaximumPreviousTransactionCount - previousCount)
			{
				throw new ArgumentOutOfRangeException(
					nameof(previousTransactionIdsBySelectedInput),
					"The ordinary-wallet plan previous-transaction limit was exceeded.");
			}
			aggregatePreviousCount += previousCount;

			string candidateId = selectedEntries[selectedIndex].OutPoint.TransactionId.CanonicalRpcHex;
			var rowPreviousIds = new string[previousCount];
			string? priorId = null;
			for (int previousIndex = 0; previousIndex < previousCount; previousIndex++)
			{
				LiquidTransactionId previousId;
				try
				{
					previousId = LiquidTransactionId.ParseRpcHex(
						sourcePreviousIds[previousIndex]!,
						nameof(previousTransactionIdsBySelectedInput));
				}
				catch (ArgumentException exception)
				{
					throw new ArgumentException(
						"Every previous transaction requires a canonical nonzero identifier.",
						nameof(previousTransactionIdsBySelectedInput),
						exception);
				}

				string normalizedId = previousId.CanonicalRpcHex;
				if (previousId.IsZero
					|| StringComparer.Ordinal.Equals(normalizedId, candidateId)
					|| priorId is not null && StringComparer.Ordinal.Compare(priorId, normalizedId) >= 0)
				{
					throw new ArgumentException(
						"Previous transaction identifiers must be canonical, unique, and strictly ordered.",
						nameof(previousTransactionIdsBySelectedInput));
				}

				rowPreviousIds[previousIndex] = normalizedId;
				blockHashesByTransactionId.TryAdd(normalizedId, null);
				priorId = normalizedId;
			}
			normalizedPreviousTransactionIds[selectedIndex] = rowPreviousIds;
			if (previousIdsByCandidateId.TryGetValue(candidateId, out string[]? priorPreviousIds))
			{
				if (!priorPreviousIds.AsSpan().SequenceEqual(rowPreviousIds))
				{
					throw new ArgumentException(
						"Selected inputs from one transaction must have one previous-transaction row.",
						nameof(previousTransactionIdsBySelectedInput));
				}
			}
			else
			{
				previousIdsByCandidateId.Add(candidateId, rowPreviousIds);
			}
		}

		if (blockHashesByTransactionId.Count is < 1 or > MaxRawTransactionCount)
		{
			throw new ArgumentOutOfRangeException(
				nameof(previousTransactionIdsBySelectedInput),
				"The ordinary-wallet plan raw-transaction request limit was exceeded.");
		}

		string[] transactionIds = [.. blockHashesByTransactionId.Keys];
		Array.Sort(transactionIds, StringComparer.Ordinal);
		var requests = new ElementsRawTransactionRequest[transactionIds.Length];
		for (int index = 0; index < transactionIds.Length; index++)
		{
			string transactionId = transactionIds[index];
			requests[index] = new ElementsRawTransactionRequest(
				transactionId,
				blockHashesByTransactionId[transactionId]);
		}

		return (requests, normalizedPreviousTransactionIds);
	}

	private const int MaxRawTransactionBytes = 4_194_304;
	private const int MaxRawTransactionBatchBytes = 67_108_864;
	private const int MaxRawTransactionCount = 100;
	private const int MaxRawTransactionHexBytes = MaxRawTransactionBytes * 2;
	private const int MaxRawTransactionRpcResponseBytes = MaxRawTransactionHexBytes + 1024;

	private sealed record RpcRequest(string Jsonrpc, string Id, string Method, object[] Params);

	private struct JsonContainerFrame(bool isArray)
	{
		public bool IsArray { get; } = isArray;
		public int Count { get; set; }
	}
}
