using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.Assets;
using WalletWasabi.Liquid.Network;

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
			return await CallCoreAsync(method, parameters, totalTimeout.Token).ConfigureAwait(false);
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
			_timeouts.ResponseIdleTimeout,
			cancellationToken).ConfigureAwait(false);
		try
		{
			ValidateJsonResourceLimits(responseBytes, method);
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
		TimeSpan responseIdleTimeout,
		CancellationToken cancellationToken)
	{
		if (content.Headers.ContentLength is > MaxRpcResponseBytes)
		{
			throw ProtocolFailure(method, "the response exceeded the one-megabyte limit");
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
				if (output.Length + read > MaxRpcResponseBytes)
				{
					throw ProtocolFailure(method, "the response exceeded the one-megabyte limit");
				}

				output.Write(buffer, 0, read);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
		}
	}

	private static void ValidateJsonResourceLimits(ReadOnlySpan<byte> responseBytes, string method)
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
					RequireValueLength(reader, MaxJsonStringBytes, method, "JSON string");
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
		if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } warnings || warnings.Length > 4096)
		{
			throw InvalidResult("node status", $"field '{propertyName}' must be a bounded string");
		}

		return warnings.Length > 0;
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

	public void Dispose()
	{
		_probeLock.Dispose();
		if (_ownsHttpClient)
		{
			_httpClient.Dispose();
		}
	}

	private sealed record RpcRequest(string Jsonrpc, string Id, string Method, object[] Params);

	private struct JsonContainerFrame(bool isArray)
	{
		public bool IsArray { get; } = isArray;
		public int Count { get; set; }
	}
}
