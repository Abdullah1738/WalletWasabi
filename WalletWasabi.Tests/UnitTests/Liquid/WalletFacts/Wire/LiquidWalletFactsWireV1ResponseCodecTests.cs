using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Liquid.WalletFacts.Wire;
using Xunit;
using LiquidWalletFactsWireV1InputView = WalletWasabi.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1Response.LiquidWalletFactsWireV1InputView;
using LiquidWalletFactsWireV1OwnedOutputView = WalletWasabi.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1Response.LiquidWalletFactsWireV1OwnedOutputView;
using LiquidWalletFactsWireV1TransactionView = WalletWasabi.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1Response.LiquidWalletFactsWireV1TransactionView;

namespace WalletWasabi.Tests.UnitTests.Liquid.WalletFacts.Wire;

public class LiquidWalletFactsWireV1ResponseCodecTests
{
	private const string SourceAHex = "4141414141414141414141414141414141414141414141414141414141414141";

	[Fact]
	public void ExactCorpusPacketIsClosedAndSelfAuthenticating()
	{
		WalletFactsWireV1Corpus.AssertChecksumPacket();
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		Assert.Equal(86, frames.Count);
		Assert.Equal(55, frames.Values.Count(frame => frame.Kind == "response"));
	}

	[Fact]
	public void ReplaysEveryResponseDecodeCaseAndEveryAcceptedView()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		IReadOnlyList<string[]> cases = WalletFactsWireV1Corpus.ReadRows(
			"CASES_V1.tsv",
			"case_id",
			"frame_id",
			"operation",
			"expected_source_epoch_hex",
			"expected_status",
			"expected_error_code",
			"canonical_reencode");
		Assert.Equal(107, cases.Count);
		string[][] responseCases = cases.Where(row => row[2] == "response-decode").ToArray();
		Assert.Equal(70, responseCases.Length);
		Assert.Equal(15, responseCases.Count(row => row[4] == "ok"));
		Assert.Equal(55, responseCases.Count(row => row[4] == "error"));

		foreach (string[] row in responseCases)
		{
			CorpusFrame fixture = frames[row[1]];
			byte[] expectedSource = Convert.FromHexString(row[3]);
			uint expectedCode = uint.Parse(row[5], CultureInfo.InvariantCulture);
			LiquidWalletFactsWireV1Response? response = null;
			try
			{
				bool success = LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
					fixture.Bytes,
					expectedSource,
					out response,
					out LiquidWalletFactsWireErrorCode errorCode);

				Assert.Equal(expectedCode == 0, success);
				Assert.Equal(expectedCode, (uint)errorCode);
				Assert.Equal(expectedCode == 0, response is not null);
				if (response is not null)
				{
					AssertResponseEquals(ParseAcceptedResponse(fixture.Bytes), response);
				}
			}
			finally
			{
				response?.Dispose();
			}
		}
	}

	[Fact]
	public void ErrorCodesAndMessagesExactlyMatchTheSharedMap()
	{
		Assert.Equal(typeof(uint), Enum.GetUnderlyingType(typeof(LiquidWalletFactsWireErrorCode)));
		Assert.Equal(
			new[]
			{
				"None",
				"InvalidArgument",
				"VersionMismatch",
				"InvalidEncoding",
				"LimitExceeded",
				"DescriptorRejected",
				"CandidateRejected",
				"ObservationRejected",
				"SourceBindingMismatch",
			},
			Enum.GetNames<LiquidWalletFactsWireErrorCode>());
		Assert.Equal(Enumerable.Range(0, 9).Select(value => (uint)value), Enum.GetValues<LiquidWalletFactsWireErrorCode>().Select(value => (uint)value));

		string text = WalletFactsWireV1Corpus.ReadCanonicalText(
			Path.Combine(WalletFactsWireV1Corpus.RootPath, "ERROR_MAPPING_V1.tsv"));
		string[] lines = text[..^1].Split('\n');
		Assert.Equal("code\tvariant\ttext", lines[0]);
		Assert.Equal(9, lines.Length);
		foreach (string line in lines.Skip(1))
		{
			string[] fields = line.Split('\t');
			Assert.Equal(3, fields.Length);
			var code = (LiquidWalletFactsWireErrorCode)uint.Parse(fields[0], CultureInfo.InvariantCulture);
			Assert.Equal(fields[1], code.ToString());
			Assert.Equal(fields[2], code.GetMessage());
		}

		ArgumentOutOfRangeException none = Assert.Throws<ArgumentOutOfRangeException>(
			() => LiquidWalletFactsWireErrorCode.None.GetMessage());
		ArgumentOutOfRangeException unknown = Assert.Throws<ArgumentOutOfRangeException>(
			() => ((LiquidWalletFactsWireErrorCode)9).GetMessage());
		Assert.Null(none.ActualValue);
		Assert.Null(unknown.ActualValue);
		Assert.Equal(none.Message, unknown.Message);
	}

	[Fact]
	public void RejectsInvalidExpectedSourceLengthsBeforeInspectingTheFrame()
	{
		foreach (byte[] source in new[] { new byte[31], Enumerable.Repeat((byte)1, 33).ToArray() })
		{
			bool success = LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
				[],
				source,
				out LiquidWalletFactsWireV1Response? response,
				out LiquidWalletFactsWireErrorCode errorCode);
			Assert.False(success);
			Assert.Null(response);
			Assert.Equal(LiquidWalletFactsWireErrorCode.InvalidArgument, errorCode);
			response?.Dispose();
		}
	}

	[Fact]
	public void CallerMutationCannotChangePublishedFactsAndGettersAreDefensive()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		byte[] frame = [.. frames["response-06-base-multi-asset"].Bytes];
		byte[] expectedSource = Convert.FromHexString(SourceAHex);
		ExpectedResponse expected = ParseAcceptedResponse(frame);

		LiquidWalletFactsWireV1Response? response = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
				frame,
				expectedSource,
				out response,
				out LiquidWalletFactsWireErrorCode errorCode));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
			LiquidWalletFactsWireV1Response ownedResponse = Assert.IsType<LiquidWalletFactsWireV1Response>(response);
			frame.AsSpan().Clear();
			expectedSource.AsSpan().Clear();
			AssertResponseEquals(expected, ownedResponse);

			LiquidWalletFactsWireV1TransactionView transaction = ownedResponse.GetTransaction(0);
			LiquidWalletFactsWireV1InputView input = transaction.GetInput(0);
			LiquidWalletFactsWireV1OwnedOutputView output = transaction.GetOwnedOutput(0);
			AssertDefensive(ownedResponse.GetSourceEpoch);
			AssertDefensive(transaction.GetTransactionIdConsensusBytes);
			AssertDefensive(transaction.GetTransactionWitnessBinding);
			AssertDefensive(input.GetPreviousTransactionIdConsensusBytes);
			AssertDefensive(output.GetScriptPubKey);
			AssertDefensive(output.GetSpendPublicKey);
			AssertDefensive(output.GetBlindingPublicKey);
			AssertDefensive(output.GetAssetIdConsensusBytes);
		}
		finally
		{
			response?.Dispose();
		}
	}

	[Fact]
	public async Task ConcurrentCallerMutationCannotPublishUnboundOrPartialFactsAsync()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		byte[] sourceA = Convert.FromHexString(SourceAHex);
		byte[] sourceB = Enumerable.Repeat((byte)'B', 32).ToArray();

		byte[] mutableFrame = [.. frames["response-06-base-multi-asset"].Bytes];
		ExpectedResponse expected = ParseAcceptedResponse(mutableFrame);
		await ExerciseConcurrentDecodeMutationAsync(
			mutableFrame,
			sourceA,
			mutableFrame,
			32,
			sourceA,
			sourceB,
			expected);

		byte[] fixedFrame = [.. frames["response-06-base-multi-asset"].Bytes];
		byte[] mutableExpectedSource = [.. sourceA];
		await ExerciseConcurrentDecodeMutationAsync(
			fixedFrame,
			mutableExpectedSource,
			mutableExpectedSource,
			0,
			sourceA,
			sourceB,
			expected);
	}

	[Fact]
	public void AcceptsZeroWitnessAndCrossParentInputReuseButRejectsSameParentDuplicate()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		byte[] expectedSource = Convert.FromHexString(SourceAHex);
		byte[] zeroWitness = [.. frames["response-03-base-one-output"].Bytes];
		zeroWitness.AsSpan(96, 32).Clear();

		LiquidWalletFactsWireV1Response? zeroWitnessResponse = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
				zeroWitness,
				expectedSource,
				out zeroWitnessResponse,
				out LiquidWalletFactsWireErrorCode zeroWitnessError));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, zeroWitnessError);
			LiquidWalletFactsWireV1Response ownedZeroWitnessResponse = Assert.IsType<LiquidWalletFactsWireV1Response>(zeroWitnessResponse);
			Assert.Equal(new byte[32], ownedZeroWitnessResponse.GetTransaction(0).GetTransactionWitnessBinding());
		}
		finally
		{
			zeroWitnessResponse?.Dispose();
		}

		LiquidWalletFactsWireV1Response? multiassetResponse = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
				frames["response-06-base-multi-asset"].Bytes,
				expectedSource,
				out multiassetResponse,
				out LiquidWalletFactsWireErrorCode multiassetError));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, multiassetError);
			LiquidWalletFactsWireV1Response ownedMultiassetResponse = Assert.IsType<LiquidWalletFactsWireV1Response>(multiassetResponse);
			LiquidWalletFactsWireV1InputView first = ownedMultiassetResponse.GetTransaction(0).GetInput(0);
			LiquidWalletFactsWireV1InputView second = ownedMultiassetResponse.GetTransaction(1).GetInput(0);
			Assert.Equal(first.GetPreviousTransactionIdConsensusBytes(), second.GetPreviousTransactionIdConsensusBytes());
			Assert.Equal(first.PreviousOutputIndex, second.PreviousOutputIndex);
		}
		finally
		{
			multiassetResponse?.Dispose();
		}

		Assert.False(LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
			frames["response-25-duplicate-input-first"].Bytes,
			expectedSource,
			out LiquidWalletFactsWireV1Response? duplicateResponse,
			out LiquidWalletFactsWireErrorCode duplicateError));
		Assert.Null(duplicateResponse);
		Assert.Equal(LiquidWalletFactsWireErrorCode.InvalidEncoding, duplicateError);
		duplicateResponse?.Dispose();
	}

	[Fact]
	public void DisposalClearsOwnerStorageAndInvalidatesAllExistingViews()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		LiquidWalletFactsWireV1Response? decoded = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
				frames["response-03-base-one-output"].Bytes,
				Convert.FromHexString(SourceAHex),
				out decoded,
				out LiquidWalletFactsWireErrorCode errorCode));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
			LiquidWalletFactsWireV1Response response = Assert.IsType<LiquidWalletFactsWireV1Response>(decoded);
			LiquidWalletFactsWireV1TransactionView transaction = response.GetTransaction(0);
			LiquidWalletFactsWireV1InputView input = transaction.GetInput(0);
			LiquidWalletFactsWireV1OwnedOutputView output = transaction.GetOwnedOutput(0);

			FieldInfo frameField = Assert.Single(
				typeof(LiquidWalletFactsWireV1Response).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
				field => field.FieldType == typeof(byte[]));
			FieldInfo offsetField = Assert.Single(
				typeof(LiquidWalletFactsWireV1Response).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
				field => field.FieldType == typeof(int[]));
			byte[] ownedFrame = Assert.IsType<byte[]>(frameField.GetValue(response));
			int[] ownedOffsets = Assert.IsType<int[]>(offsetField.GetValue(response));
			Assert.Contains(ownedFrame, value => value != 0);
			Assert.Contains(ownedOffsets, value => value != 0);

			response.Dispose();
			Assert.All(ownedFrame, value => Assert.Equal(0, value));
			Assert.All(ownedOffsets, value => Assert.Equal(0, value));
			response.Dispose();

			Assert.Equal(nameof(LiquidWalletFactsWireV1Response), response.ToString());
			Assert.Equal(nameof(LiquidWalletFactsWireV1TransactionView), transaction.ToString());
			Assert.Equal(nameof(LiquidWalletFactsWireV1InputView), input.ToString());
			Assert.Equal(nameof(LiquidWalletFactsWireV1OwnedOutputView), output.ToString());

			Action[] accessors =
			[
				() => _ = response.TransactionCount,
			() => _ = response.OwnedOutputCount,
			() => _ = response.IsEmpty,
			() => response.GetSourceEpoch(),
			() => response.GetTransaction(-1),
			() => _ = transaction.InputCount,
			() => _ = transaction.OwnedOutputCount,
			() => transaction.GetTransactionIdConsensusBytes(),
			() => transaction.GetTransactionWitnessBinding(),
			() => transaction.GetInput(-1),
			() => transaction.GetOwnedOutput(-1),
			() => input.GetPreviousTransactionIdConsensusBytes(),
			() => _ = input.PreviousOutputIndex,
			() => _ = output.OutputIndex,
			() => _ = output.Branch,
			() => _ = output.DerivationIndex,
			() => _ = output.Value,
			() => output.GetScriptPubKey(),
			() => output.GetSpendPublicKey(),
			() => output.GetBlindingPublicKey(),
			() => output.GetAssetIdConsensusBytes(),
		];
			ObjectDisposedException[] exceptions = accessors
				.Select(action => Assert.Throws<ObjectDisposedException>(action))
				.ToArray();
			Assert.Single(exceptions.Select(exception => exception.Message).Distinct(StringComparer.Ordinal));
		}
		finally
		{
			decoded?.Dispose();
		}
	}

	[Fact]
	public async Task ConcurrentGettersAndDisposeReturnCompleteFactsOrTheFixedDisposedErrorAsync()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		ExpectedResponse expected = ParseAcceptedResponse(frames["response-06-base-multi-asset"].Bytes);
		LiquidWalletFactsWireV1Response? decoded = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
				frames["response-06-base-multi-asset"].Bytes,
				Convert.FromHexString(SourceAHex),
				out decoded,
				out LiquidWalletFactsWireErrorCode errorCode));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
			LiquidWalletFactsWireV1Response response = Assert.IsType<LiquidWalletFactsWireV1Response>(decoded);

			using var start = new ManualResetEventSlim();
			using var releaseRace = new ManualResetEventSlim();
			using var completedFirstReads = new CountdownEvent(4);
			Task[] readers = Enumerable.Range(0, 4)
				.Select(_ => Task.Run(() =>
				{
					start.Wait();
					try
					{
						AssertResponseEquals(expected, response);
					}
					finally
					{
						completedFirstReads.Signal();
					}

					releaseRace.Wait();
					for (int iteration = 0; iteration < 512; iteration++)
					{
						try
						{
							AssertResponseEquals(expected, response);
						}
						catch (ObjectDisposedException exception)
						{
							Assert.Equal(nameof(LiquidWalletFactsWireV1Response), exception.ObjectName);
							return;
						}
					}
				}))
				.ToArray();
			Task disposer = Task.Run(() =>
			{
				releaseRace.Wait();
				response.Dispose();
			});

			start.Set();
			Assert.True(completedFirstReads.Wait(TimeSpan.FromSeconds(10)));
			releaseRace.Set();
			await Task.WhenAll([.. readers, disposer]);
			Assert.Throws<ObjectDisposedException>(() => response.GetSourceEpoch());
		}
		finally
		{
			decoded?.Dispose();
		}
	}

	[Fact]
	public void IndexedAccessIsBoundedAndPrivacyRedacted()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		LiquidWalletFactsWireV1Response? decoded = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
				frames["response-03-base-one-output"].Bytes,
				Convert.FromHexString(SourceAHex),
				out decoded,
				out _));
			LiquidWalletFactsWireV1Response response = Assert.IsType<LiquidWalletFactsWireV1Response>(decoded);
			LiquidWalletFactsWireV1TransactionView transaction = response.GetTransaction(0);
			ArgumentOutOfRangeException[] errors =
			[
				Assert.Throws<ArgumentOutOfRangeException>(() => response.GetTransaction(-1)),
				Assert.Throws<ArgumentOutOfRangeException>(() => response.GetTransaction(response.TransactionCount)),
				Assert.Throws<ArgumentOutOfRangeException>(() => transaction.GetInput(-1)),
				Assert.Throws<ArgumentOutOfRangeException>(() => transaction.GetInput(transaction.InputCount)),
				Assert.Throws<ArgumentOutOfRangeException>(() => transaction.GetOwnedOutput(-1)),
				Assert.Throws<ArgumentOutOfRangeException>(() => transaction.GetOwnedOutput(transaction.OwnedOutputCount)),
			];
			Assert.All(errors, error => Assert.Null(error.ActualValue));
		}
		finally
		{
			decoded?.Dispose();
		}
	}

	[Fact]
	public void ProductionSurfaceKeepsExactLimitsAndNoForbiddenCapability()
	{
		Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(LiquidWalletFactsWireV1Branch)));
		Assert.Equal(
			new[] { "External", "Internal" },
			Enum.GetNames<LiquidWalletFactsWireV1Branch>());
		Assert.Equal(new byte[] { 0, 1 }, Enum.GetValues<LiquidWalletFactsWireV1Branch>().Select(value => (byte)value));

		Type[] roots =
		[
			typeof(LiquidWalletFactsWireErrorCode),
			typeof(LiquidWalletFactsWireErrorCodeExtensions),
			typeof(LiquidWalletFactsWireV1Branch),
			typeof(LiquidWalletFactsWireV1ResponseCodec),
			typeof(LiquidWalletFactsWireV1Response),
			typeof(LiquidWalletFactsWireV1TransactionView),
			typeof(LiquidWalletFactsWireV1InputView),
			typeof(LiquidWalletFactsWireV1OwnedOutputView),
		];
		Type[] productionTypes = roots
			.SelectMany(TypeAndNestedTypes)
			.Distinct()
			.ToArray();
		Assert.All(productionTypes, type => Assert.False(type.IsPublic));
		Assert.True(typeof(LiquidWalletFactsWireV1ResponseCodec).IsAbstract && typeof(LiquidWalletFactsWireV1ResponseCodec).IsSealed);
		Assert.True(typeof(LiquidWalletFactsWireV1Response).IsSealed);
		Assert.Equal(new[] { typeof(IDisposable) }, typeof(LiquidWalletFactsWireV1Response).GetInterfaces());
		Assert.Empty(typeof(LiquidWalletFactsWireV1TransactionView).GetInterfaces());
		Assert.Empty(typeof(LiquidWalletFactsWireV1InputView).GetInterfaces());
		Assert.Empty(typeof(LiquidWalletFactsWireV1OwnedOutputView).GetInterfaces());
		BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		AssertNonPrivateDeclaredMemberNames(typeof(LiquidWalletFactsWireErrorCode));
		AssertNonPrivateDeclaredMemberNames(typeof(LiquidWalletFactsWireErrorCodeExtensions), "GetMessage");
		AssertNonPrivateDeclaredMemberNames(typeof(LiquidWalletFactsWireV1Branch));
		AssertNonPrivateDeclaredMemberNames(typeof(LiquidWalletFactsWireV1ResponseCodec), "TryDecodeResponse");
		AssertNonPrivateDeclaredMemberNames(
			typeof(LiquidWalletFactsWireV1Response),
			".ctor",
			"Dispose",
			"GetSourceEpoch",
			"GetTransaction",
			"ToString",
			"get_IsEmpty",
			"get_OwnedOutputCount",
			"get_TransactionCount");
		AssertNonPrivateDeclaredMemberNames(
			typeof(LiquidWalletFactsWireV1TransactionView),
			".ctor",
			"GetInput",
			"GetOwnedOutput",
			"GetTransactionIdConsensusBytes",
			"GetTransactionWitnessBinding",
			"ToString",
			"get_InputCount",
			"get_OwnedOutputCount");
		AssertNonPrivateDeclaredMemberNames(
			typeof(LiquidWalletFactsWireV1InputView),
			".ctor",
			"GetPreviousTransactionIdConsensusBytes",
			"ToString",
			"get_PreviousOutputIndex");
		AssertNonPrivateDeclaredMemberNames(
			typeof(LiquidWalletFactsWireV1OwnedOutputView),
			".ctor",
			"GetAssetIdConsensusBytes",
			"GetBlindingPublicKey",
			"GetScriptPubKey",
			"GetSpendPublicKey",
			"ToString",
			"get_Branch",
			"get_DerivationIndex",
			"get_OutputIndex",
			"get_Value");
		Assert.All(
			new[]
			{
				typeof(LiquidWalletFactsWireV1TransactionView),
				typeof(LiquidWalletFactsWireV1InputView),
				typeof(LiquidWalletFactsWireV1OwnedOutputView),
			},
			type => Assert.All(type.GetConstructors(declared), constructor => Assert.True(constructor.IsAssembly)));

		foreach (Type type in productionTypes)
		{
			foreach (MethodBase method in type.GetMethods(declared).Cast<MethodBase>().Concat(type.GetConstructors(declared)))
			{
				Assert.False((method.Attributes & MethodAttributes.PinvokeImpl) != 0);
				Assert.DoesNotContain(
					method.GetCustomAttributesData(),
					attribute => attribute.AttributeType.FullName == "System.Runtime.InteropServices.DllImportAttribute");
				Assert.False(ContainsPointer(method.DeclaringType!));
				Assert.All(method.GetParameters(), parameter => Assert.False(ContainsPointer(parameter.ParameterType)));
				if (method is MethodInfo methodInfo)
				{
					Assert.False(ContainsPointer(methodInfo.ReturnType));
				}

				foreach (MemberInfo referenced in ReferencedMembers(method))
				{
					Type? referencedType = referenced as Type ?? referenced.DeclaringType;
					Assert.False(IsForbiddenCapability(referencedType), $"Forbidden reference: {referenced}");
				}
			}
		}

		ulong[] literalValues = typeof(LiquidWalletFactsWireV1ResponseCodec)
			.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
			.Where(field => field.IsLiteral && field.GetRawConstantValue() is not null)
			.Select(field => Convert.ToUInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture))
			.ToArray();
		ulong[] requiredLimits =
		[
			268_435_456,
			80_599_492,
			4_096,
			1_636_801,
			148_470,
			102_298,
			9_279,
			0x3fff_ffff,
			100_000,
			0x7fff_ffff_ffff_ffff,
		];
		Assert.All(requiredLimits, value => Assert.Contains(value, literalValues));
	}

	private static async Task ExerciseConcurrentDecodeMutationAsync(
		byte[] frame,
		byte[] expectedSource,
		byte[] mutationTarget,
		int mutationOffset,
		byte[] sourceA,
		byte[] sourceB,
		ExpectedResponse expected)
	{
		using var start = new ManualResetEventSlim();
		int stop = 0;
		Task mutator = Task.Run(() =>
		{
			start.Wait();
			while (Volatile.Read(ref stop) == 0)
			{
				sourceB.CopyTo(mutationTarget.AsSpan(mutationOffset, sourceB.Length));
				Thread.SpinWait(64);
				sourceA.CopyTo(mutationTarget.AsSpan(mutationOffset, sourceA.Length));
				Thread.SpinWait(256);
			}
		});

		int acceptedCount = 0;
		start.Set();
		try
		{
			for (int iteration = 0; iteration < 2_048; iteration++)
			{
				LiquidWalletFactsWireV1Response? response = null;
				try
				{
					bool accepted = LiquidWalletFactsWireV1ResponseCodec.TryDecodeResponse(
						frame,
						expectedSource,
						out response,
						out LiquidWalletFactsWireErrorCode errorCode);
					if (accepted)
					{
						acceptedCount++;
						Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
						LiquidWalletFactsWireV1Response ownedResponse = Assert.IsType<LiquidWalletFactsWireV1Response>(response);
						Assert.Equal(sourceA, ownedResponse.GetSourceEpoch());
						AssertResponseEquals(expected, ownedResponse);
					}
					else
					{
						Assert.Null(response);
						Assert.Equal(LiquidWalletFactsWireErrorCode.SourceBindingMismatch, errorCode);
					}
				}
				finally
				{
					response?.Dispose();
				}
			}
		}
		finally
		{
			Volatile.Write(ref stop, 1);
			await mutator;
			sourceA.CopyTo(mutationTarget.AsSpan(mutationOffset, sourceA.Length));
		}

		Assert.True(acceptedCount > 0);
	}

	private static void AssertNonPrivateDeclaredMemberNames(Type type, params string[] expectedNames)
	{
		BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		string[] actualNames = type.GetMethods(declared)
			.Cast<MethodBase>()
			.Concat(type.GetConstructors(declared))
			.Where(member => !member.IsPrivate)
			.Select(member => member.Name)
			.Order(StringComparer.Ordinal)
			.ToArray();
		Assert.Equal(expectedNames.Order(StringComparer.Ordinal), actualNames);
	}

	private static void AssertDefensive(Func<byte[]> getter)
	{
		byte[] first = getter();
		byte[] expected = [.. first];
		byte[] second = getter();
		Assert.NotSame(first, second);
		first.AsSpan().Clear();
		Assert.Equal(expected, getter());
	}

	private static IEnumerable<Type> TypeAndNestedTypes(Type type)
	{
		yield return type;
		foreach (Type nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
		{
			foreach (Type descendant in TypeAndNestedTypes(nested))
			{
				yield return descendant;
			}
		}
	}

	private static bool ContainsPointer(Type type)
	{
		if (type.IsPointer || type.IsFunctionPointer)
		{
			return true;
		}
		if (type.HasElementType)
		{
			return ContainsPointer(type.GetElementType()!);
		}
		return type.IsGenericType && type.GetGenericArguments().Any(ContainsPointer);
	}

	private static bool IsForbiddenCapability(Type? type)
	{
		if (type is null)
		{
			return false;
		}
		string name = type.FullName ?? type.Name;
		return name is "System.Runtime.InteropServices.NativeLibrary" or "System.Runtime.InteropServices.Marshal" or "System.Diagnostics.Process" ||
			name.StartsWith("System.Net.", StringComparison.Ordinal) ||
			name.StartsWith("System.IO.File", StringComparison.Ordinal) ||
			name.StartsWith("System.IO.Directory", StringComparison.Ordinal) ||
			name.StartsWith("WalletWasabi.Liquid.Rpc.", StringComparison.Ordinal) ||
			name.StartsWith("WalletWasabi.WabiSabi.", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletState", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletTransactionDelta", StringComparison.Ordinal) ||
			name.Contains("LiquidOwnedOutputObservation", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletTransactionObservation", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletObservationBatch", StringComparison.Ordinal) ||
			name.Contains("CoinJoin", StringComparison.Ordinal) ||
			name.Contains("Broadcast", StringComparison.Ordinal) ||
			name.Contains("Signer", StringComparison.Ordinal) ||
			name.Contains("Pset", StringComparison.Ordinal);
	}

	private static IEnumerable<MemberInfo> ReferencedMembers(MethodBase method)
	{
		MethodBody? body = method.GetMethodBody();
		byte[]? il = body?.GetILAsByteArray();
		if (il is null)
		{
			yield break;
		}

		int offset = 0;
		while (offset < il.Length)
		{
			short value = il[offset++] == 0xfe
				? unchecked((short)(0xfe00 | il[offset++]))
				: il[offset - 1];
			OpCode opcode = OpcodeByValue[value];
			switch (opcode.OperandType)
			{
				case OperandType.InlineField:
				case OperandType.InlineMethod:
				case OperandType.InlineTok:
				case OperandType.InlineType:
					{
						int token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, sizeof(int)));
						offset += sizeof(int);
						MemberInfo? member = method.Module.ResolveMember(
							token,
							method.DeclaringType?.GetGenericArguments(),
							method.IsGenericMethod ? method.GetGenericArguments() : null);
						if (member is not null)
						{
							yield return member;
						}
						break;
					}
				case OperandType.InlineBrTarget:
				case OperandType.InlineI:
				case OperandType.InlineSig:
				case OperandType.InlineString:
				case OperandType.ShortInlineR:
					offset += 4;
					break;
				case OperandType.InlineI8:
				case OperandType.InlineR:
					offset += 8;
					break;
				case OperandType.InlineVar:
					offset += 2;
					break;
				case OperandType.ShortInlineBrTarget:
				case OperandType.ShortInlineI:
				case OperandType.ShortInlineVar:
					offset += 1;
					break;
				case OperandType.InlineSwitch:
					int count = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, sizeof(int)));
					offset += sizeof(int) + checked(count * sizeof(int));
					break;
				case OperandType.InlineNone:
					break;
				default:
					throw new InvalidOperationException($"Unsupported IL operand: {opcode.OperandType}");
			}
		}
	}

	private static readonly IReadOnlyDictionary<short, OpCode> OpcodeByValue = typeof(OpCodes)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(field => field.FieldType == typeof(OpCode))
		.Select(field => (OpCode)field.GetValue(null)!)
		.ToDictionary(opcode => opcode.Value);

	private static void AssertResponseEquals(ExpectedResponse expected, LiquidWalletFactsWireV1Response actual)
	{
		Assert.Equal(expected.SourceEpoch, actual.GetSourceEpoch());
		Assert.Equal(expected.Transactions.Length, actual.TransactionCount);
		Assert.Equal(expected.Transactions.Sum(transaction => transaction.Outputs.Length), actual.OwnedOutputCount);
		Assert.Equal(expected.Transactions.Length == 0, actual.IsEmpty);
		for (int transactionIndex = 0; transactionIndex < expected.Transactions.Length; transactionIndex++)
		{
			ExpectedTransaction expectedTransaction = expected.Transactions[transactionIndex];
			LiquidWalletFactsWireV1TransactionView actualTransaction = actual.GetTransaction(transactionIndex);
			Assert.Equal(expectedTransaction.TransactionId, actualTransaction.GetTransactionIdConsensusBytes());
			Assert.Equal(expectedTransaction.WitnessBinding, actualTransaction.GetTransactionWitnessBinding());
			Assert.Equal(expectedTransaction.Inputs.Length, actualTransaction.InputCount);
			Assert.Equal(expectedTransaction.Outputs.Length, actualTransaction.OwnedOutputCount);
			for (int inputIndex = 0; inputIndex < expectedTransaction.Inputs.Length; inputIndex++)
			{
				ExpectedInput expectedInput = expectedTransaction.Inputs[inputIndex];
				LiquidWalletFactsWireV1InputView actualInput = actualTransaction.GetInput(inputIndex);
				Assert.Equal(expectedInput.PreviousTransactionId, actualInput.GetPreviousTransactionIdConsensusBytes());
				Assert.Equal(expectedInput.PreviousOutputIndex, actualInput.PreviousOutputIndex);
			}
			for (int outputIndex = 0; outputIndex < expectedTransaction.Outputs.Length; outputIndex++)
			{
				ExpectedOutput expectedOutput = expectedTransaction.Outputs[outputIndex];
				LiquidWalletFactsWireV1OwnedOutputView actualOutput = actualTransaction.GetOwnedOutput(outputIndex);
				Assert.Equal(expectedOutput.OutputIndex, actualOutput.OutputIndex);
				Assert.Equal((LiquidWalletFactsWireV1Branch)expectedOutput.Branch, actualOutput.Branch);
				Assert.Equal(expectedOutput.DerivationIndex, actualOutput.DerivationIndex);
				Assert.Equal(expectedOutput.Value, actualOutput.Value);
				Assert.Equal(expectedOutput.ScriptPubKey, actualOutput.GetScriptPubKey());
				Assert.Equal(expectedOutput.SpendPublicKey, actualOutput.GetSpendPublicKey());
				Assert.Equal(expectedOutput.BlindingPublicKey, actualOutput.GetBlindingPublicKey());
				Assert.Equal(expectedOutput.AssetId, actualOutput.GetAssetIdConsensusBytes());
			}
		}
	}

	private static ExpectedResponse ParseAcceptedResponse(ReadOnlySpan<byte> frame)
	{
		int cursor = 0;
		Assert.True(Take(frame, ref cursor, 4).SequenceEqual("WLFV"u8));
		Assert.Equal((ushort)1, ReadUInt16(frame, ref cursor));
		Assert.Equal((ushort)64, ReadUInt16(frame, ref cursor));
		Assert.Equal((ulong)frame.Length, ReadUInt64(frame, ref cursor));
		Assert.Equal(0u, ReadUInt32(frame, ref cursor));
		int transactionCount = checked((int)ReadUInt32(frame, ref cursor));
		int aggregateOutputCount = checked((int)ReadUInt32(frame, ref cursor));
		Assert.Equal(0u, ReadUInt32(frame, ref cursor));
		byte[] sourceEpoch = Take(frame, ref cursor, 32).ToArray();
		var transactions = new ExpectedTransaction[transactionCount];
		int observedOutputs = 0;
		for (int transactionIndex = 0; transactionIndex < transactions.Length; transactionIndex++)
		{
			byte[] transactionId = Take(frame, ref cursor, 32).ToArray();
			byte[] witnessBinding = Take(frame, ref cursor, 32).ToArray();
			int inputCount = checked((int)ReadUInt32(frame, ref cursor));
			int outputCount = checked((int)ReadUInt32(frame, ref cursor));
			var inputs = new ExpectedInput[inputCount];
			for (int inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
			{
				inputs[inputIndex] = new ExpectedInput(
					Take(frame, ref cursor, 32).ToArray(),
					ReadUInt32(frame, ref cursor));
			}
			var outputs = new ExpectedOutput[outputCount];
			for (int outputIndex = 0; outputIndex < outputs.Length; outputIndex++)
			{
				uint index = ReadUInt32(frame, ref cursor);
				int scriptLength = checked((int)ReadUInt32(frame, ref cursor));
				byte[] spendPublicKey = Take(frame, ref cursor, 33).ToArray();
				byte[] blindingPublicKey = Take(frame, ref cursor, 33).ToArray();
				byte branch = Take(frame, ref cursor, 1)[0];
				Assert.Equal(new byte[3], Take(frame, ref cursor, 3).ToArray());
				uint derivationIndex = ReadUInt32(frame, ref cursor);
				byte[] assetId = Take(frame, ref cursor, 32).ToArray();
				ulong value = ReadUInt64(frame, ref cursor);
				byte[] scriptPubKey = Take(frame, ref cursor, scriptLength).ToArray();
				outputs[outputIndex] = new ExpectedOutput(
					index,
					spendPublicKey,
					blindingPublicKey,
					branch,
					derivationIndex,
					assetId,
					value,
					scriptPubKey);
			}
			observedOutputs = checked(observedOutputs + outputs.Length);
			transactions[transactionIndex] = new ExpectedTransaction(transactionId, witnessBinding, inputs, outputs);
		}
		Assert.Equal(aggregateOutputCount, observedOutputs);
		Assert.Equal(frame.Length, cursor);
		return new ExpectedResponse(sourceEpoch, transactions);
	}

	private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> frame, ref int cursor, int length)
	{
		ReadOnlySpan<byte> value = frame.Slice(cursor, length);
		cursor = checked(cursor + length);
		return value;
	}

	private static ushort ReadUInt16(ReadOnlySpan<byte> frame, ref int cursor) =>
		BinaryPrimitives.ReadUInt16LittleEndian(Take(frame, ref cursor, sizeof(ushort)));

	private static uint ReadUInt32(ReadOnlySpan<byte> frame, ref int cursor) =>
		BinaryPrimitives.ReadUInt32LittleEndian(Take(frame, ref cursor, sizeof(uint)));

	private static ulong ReadUInt64(ReadOnlySpan<byte> frame, ref int cursor) =>
		BinaryPrimitives.ReadUInt64LittleEndian(Take(frame, ref cursor, sizeof(ulong)));

	private sealed record ExpectedResponse(byte[] SourceEpoch, ExpectedTransaction[] Transactions);

	private sealed record ExpectedTransaction(
		byte[] TransactionId,
		byte[] WitnessBinding,
		ExpectedInput[] Inputs,
		ExpectedOutput[] Outputs);

	private sealed record ExpectedInput(byte[] PreviousTransactionId, uint PreviousOutputIndex);

	private sealed record ExpectedOutput(
		uint OutputIndex,
		byte[] SpendPublicKey,
		byte[] BlindingPublicKey,
		byte Branch,
		uint DerivationIndex,
		byte[] AssetId,
		ulong Value,
		byte[] ScriptPubKey);
}
