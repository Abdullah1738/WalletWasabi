using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using WalletWasabi.Liquid.WalletFacts.Wire;
using Xunit;
using LiquidWalletFactsWireV1UntrustedStructuralInputView = WalletWasabi.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1UntrustedStructuralResponse.LiquidWalletFactsWireV1UntrustedStructuralInputView;
using LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView = WalletWasabi.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1UntrustedStructuralResponse.LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView;
using LiquidWalletFactsWireV1UntrustedStructuralTransactionView = WalletWasabi.Liquid.WalletFacts.Wire.LiquidWalletFactsWireV1UntrustedStructuralResponse.LiquidWalletFactsWireV1UntrustedStructuralTransactionView;

namespace WalletWasabi.Tests.UnitTests.Liquid.WalletFacts.Wire;

public class LiquidWalletFactsWireV1UntrustedStructuralResponseTests
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
	public void ScratchCleanupClosureRejectsAliasesAndPostZeroizationWrites()
	{
		const string Fixture = """
			class Fixture
			{
				static void ByValueAlias()
				{
					Span<byte> scratch = stackalloc byte[32];
					Span<byte> alias = scratch;
					try { }
					finally
					{
						CryptographicOperations.ZeroMemory(scratch);
						alias.Fill(0xff);
					}
				}

				static void RefAlias()
				{
					Span<byte> scratch = stackalloc byte[32];
					ref Span<byte> alias = ref scratch;
					try { }
					finally
					{
						CryptographicOperations.ZeroMemory(scratch);
						alias = default;
					}
				}

				static void AssignmentAlias()
				{
					Span<byte> alias = default;
					Span<byte> scratch = stackalloc byte[32];
					alias = scratch;
					try { }
					finally
					{
						CryptographicOperations.ZeroMemory(scratch);
						alias.Fill(0xff);
					}
				}

				static void DirectPostZeroizationWrite()
				{
					Span<byte> scratch = stackalloc byte[32];
					try { }
					finally
					{
						CryptographicOperations.ZeroMemory(scratch);
						scratch.Fill(0xff);
					}
				}

				static void AssignmentAliasNoTrivia(){Span<byte> alias=default;Span<byte> scratch=stackalloc byte[32];alias=scratch;try{}finally{CryptographicOperations.ZeroMemory(scratch);alias.Fill(0xff);}}

				static void DirectPostZeroizationWriteNoTrivia(){Span<byte> scratch=stackalloc byte[32];try{}finally{CryptographicOperations.ZeroMemory(scratch);scratch.Fill(0xff);}}

				static void AfterFinallyWrite(){Span<byte> scratch=stackalloc byte[32];try{}finally{CryptographicOperations.ZeroMemory(scratch);}scratch.Fill(0xff);}

				static void PreTryThrow(){Span<byte> scratch=stackalloc byte[32];if(true){throw new Exception();}try{}finally{CryptographicOperations.ZeroMemory(scratch);}}
			}
			""";
		var methods = CSharpSyntaxTree.ParseText(Fixture).GetRoot()
			.DescendantNodes()
			.OfType<MethodDeclarationSyntax>()
			.ToDictionary(method => method.Identifier.ValueText, StringComparer.Ordinal);

		foreach ((string methodName, bool expectedAliasExposure, bool expectedPostZeroizationUse) in new[]
		{
			("ByValueAlias", true, false),
			("RefAlias", true, false),
			("AssignmentAlias", true, false),
			("DirectPostZeroizationWrite", false, true),
			("AssignmentAliasNoTrivia", true, false),
			("DirectPostZeroizationWriteNoTrivia", false, true),
			("AfterFinallyWrite", false, true),
		})
		{
			MethodDeclarationSyntax method = methods[methodName];
			LocalDeclarationStatementSyntax declaration = Assert.Single(
				method.Body!.Statements.OfType<LocalDeclarationStatementSyntax>(),
				statement => statement.Declaration.Variables.Any(variable => variable.Identifier.ValueText == "scratch"));
			InvocationExpressionSyntax zeroization = Assert.Single(
				method.DescendantNodes().OfType<InvocationExpressionSyntax>(),
				invocation => string.Concat(invocation.Expression.DescendantTokens().Select(token => token.Text)) == "CryptographicOperations.ZeroMemory");
			ExpressionStatementSyntax zeroizationStatement = Assert.IsType<ExpressionStatementSyntax>(zeroization.Parent);
			Assert.Equal(expectedAliasExposure, HasScratchAliasOrRefExposure(method, declaration, "scratch"));
			Assert.Equal(expectedPostZeroizationUse, HasScratchUseAfterZeroization(zeroizationStatement, "scratch"));
		}
		Assert.Equal(
			new[]
			{
				"LocalDeclarationStatementSyntax|Span<byte>scratch=stackallocbyte[32];",
				"IfStatementSyntax|if(true){thrownewException();}",
			},
			GetPreTryStatementShapes(methods["PreTryThrow"]));

		const string DisabledBranchFixture = """
			class ConditionalFixture
			{
				static void HiddenAlias()
				{
				#if DEBUG
					Span<byte> scratch = stackalloc byte[32];
					Span<byte> alias = scratch;
				#endif
				}
			}
			""";
		CSharpSyntaxNode disabledBranchRoot = Assert.IsAssignableFrom<CSharpSyntaxNode>(
			CSharpSyntaxTree.ParseText(DisabledBranchFixture).GetRoot());
		Assert.True(ContainsPreprocessorDirective(disabledBranchRoot));
		Assert.Empty(disabledBranchRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>());
	}

	[Fact]
	public void FriendAssemblyInventoryRejectsAdditionalOrDuplicateConsumers()
	{
		Assert.True(IsExactFriendAssemblyInventory(["WalletWasabi.Tests"]));
		Assert.False(IsExactFriendAssemblyInventory(["Arbitrary.Consumer"]));
		Assert.False(IsExactFriendAssemblyInventory(["WalletWasabi.Tests", "Arbitrary.Consumer"]));
		Assert.False(IsExactFriendAssemblyInventory(["WalletWasabi.Tests", "WalletWasabi.Tests"]));
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
			LiquidWalletFactsWireV1UntrustedStructuralResponse? response = null;
			try
			{
				bool success = LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
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
			bool success = LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
				[],
				source,
				out LiquidWalletFactsWireV1UntrustedStructuralResponse? response,
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

		LiquidWalletFactsWireV1UntrustedStructuralResponse? response = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
				frame,
				expectedSource,
				out response,
				out LiquidWalletFactsWireErrorCode errorCode));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
			LiquidWalletFactsWireV1UntrustedStructuralResponse ownedResponse = Assert.IsType<LiquidWalletFactsWireV1UntrustedStructuralResponse>(response);
			frame.AsSpan().Clear();
			expectedSource.AsSpan().Clear();
			AssertResponseEquals(expected, ownedResponse);

			LiquidWalletFactsWireV1UntrustedStructuralTransactionView transaction = ownedResponse.GetTransaction(0);
			LiquidWalletFactsWireV1UntrustedStructuralInputView input = transaction.GetInput(0);
			LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView output = transaction.GetOwnedOutput(0);
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

		LiquidWalletFactsWireV1UntrustedStructuralResponse? zeroWitnessResponse = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
				zeroWitness,
				expectedSource,
				out zeroWitnessResponse,
				out LiquidWalletFactsWireErrorCode zeroWitnessError));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, zeroWitnessError);
			LiquidWalletFactsWireV1UntrustedStructuralResponse ownedZeroWitnessResponse = Assert.IsType<LiquidWalletFactsWireV1UntrustedStructuralResponse>(zeroWitnessResponse);
			Assert.Equal(new byte[32], ownedZeroWitnessResponse.GetTransaction(0).GetTransactionWitnessBinding());
		}
		finally
		{
			zeroWitnessResponse?.Dispose();
		}

		LiquidWalletFactsWireV1UntrustedStructuralResponse? multiassetResponse = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
				frames["response-06-base-multi-asset"].Bytes,
				expectedSource,
				out multiassetResponse,
				out LiquidWalletFactsWireErrorCode multiassetError));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, multiassetError);
			LiquidWalletFactsWireV1UntrustedStructuralResponse ownedMultiassetResponse = Assert.IsType<LiquidWalletFactsWireV1UntrustedStructuralResponse>(multiassetResponse);
			LiquidWalletFactsWireV1UntrustedStructuralInputView first = ownedMultiassetResponse.GetTransaction(0).GetInput(0);
			LiquidWalletFactsWireV1UntrustedStructuralInputView second = ownedMultiassetResponse.GetTransaction(1).GetInput(0);
			Assert.Equal(first.GetPreviousTransactionIdConsensusBytes(), second.GetPreviousTransactionIdConsensusBytes());
			Assert.Equal(first.PreviousOutputIndex, second.PreviousOutputIndex);
		}
		finally
		{
			multiassetResponse?.Dispose();
		}

		Assert.False(LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
			frames["response-25-duplicate-input-first"].Bytes,
			expectedSource,
			out LiquidWalletFactsWireV1UntrustedStructuralResponse? duplicateResponse,
			out LiquidWalletFactsWireErrorCode duplicateError));
		Assert.Null(duplicateResponse);
		Assert.Equal(LiquidWalletFactsWireErrorCode.InvalidEncoding, duplicateError);
		duplicateResponse?.Dispose();
	}

	[Fact]
	public void DisposalClearsOwnerStorageAndInvalidatesAllExistingViews()
	{
		IReadOnlyDictionary<string, CorpusFrame> frames = WalletFactsWireV1Corpus.LoadFrames();
		LiquidWalletFactsWireV1UntrustedStructuralResponse? decoded = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
				frames["response-03-base-one-output"].Bytes,
				Convert.FromHexString(SourceAHex),
				out decoded,
				out LiquidWalletFactsWireErrorCode errorCode));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
			LiquidWalletFactsWireV1UntrustedStructuralResponse response = Assert.IsType<LiquidWalletFactsWireV1UntrustedStructuralResponse>(decoded);
			LiquidWalletFactsWireV1UntrustedStructuralTransactionView transaction = response.GetTransaction(0);
			LiquidWalletFactsWireV1UntrustedStructuralInputView input = transaction.GetInput(0);
			LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView output = transaction.GetOwnedOutput(0);

			FieldInfo frameField = Assert.Single(
				typeof(LiquidWalletFactsWireV1UntrustedStructuralResponse).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
				field => field.FieldType == typeof(byte[]));
			FieldInfo offsetField = Assert.Single(
				typeof(LiquidWalletFactsWireV1UntrustedStructuralResponse).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
				field => field.FieldType == typeof(int[]));
			byte[] ownedFrame = Assert.IsType<byte[]>(frameField.GetValue(response));
			int[] ownedOffsets = Assert.IsType<int[]>(offsetField.GetValue(response));
			Assert.Contains(ownedFrame, value => value != 0);
			Assert.Contains(ownedOffsets, value => value != 0);

			response.Dispose();
			Assert.All(ownedFrame, value => Assert.Equal(0, value));
			Assert.All(ownedOffsets, value => Assert.Equal(0, value));
			response.Dispose();

			Assert.Equal(nameof(LiquidWalletFactsWireV1UntrustedStructuralResponse), response.ToString());
			Assert.Equal(nameof(LiquidWalletFactsWireV1UntrustedStructuralTransactionView), transaction.ToString());
			Assert.Equal(nameof(LiquidWalletFactsWireV1UntrustedStructuralInputView), input.ToString());
			Assert.Equal(nameof(LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView), output.ToString());

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
		LiquidWalletFactsWireV1UntrustedStructuralResponse? decoded = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
				frames["response-06-base-multi-asset"].Bytes,
				Convert.FromHexString(SourceAHex),
				out decoded,
				out LiquidWalletFactsWireErrorCode errorCode));
			Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
			LiquidWalletFactsWireV1UntrustedStructuralResponse response = Assert.IsType<LiquidWalletFactsWireV1UntrustedStructuralResponse>(decoded);

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
							Assert.Equal(nameof(LiquidWalletFactsWireV1UntrustedStructuralResponse), exception.ObjectName);
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
		LiquidWalletFactsWireV1UntrustedStructuralResponse? decoded = null;
		try
		{
			Assert.True(LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
				frames["response-03-base-one-output"].Bytes,
				Convert.FromHexString(SourceAHex),
				out decoded,
				out _));
			LiquidWalletFactsWireV1UntrustedStructuralResponse response = Assert.IsType<LiquidWalletFactsWireV1UntrustedStructuralResponse>(decoded);
			LiquidWalletFactsWireV1UntrustedStructuralTransactionView transaction = response.GetTransaction(0);
			ArgumentOutOfRangeException[] errors =
			[
				Assert.Throws<ArgumentOutOfRangeException>(() => response.GetTransaction(-1)),
				Assert.Throws<ArgumentOutOfRangeException>(() => response.GetTransaction(response.TransactionCount)),
				Assert.Throws<ArgumentOutOfRangeException>(() => transaction.GetInput(-1)),
				Assert.Throws<ArgumentOutOfRangeException>(() => transaction.GetInput(transaction.InputCount)),
				Assert.Throws<ArgumentOutOfRangeException>(() => transaction.GetOwnedOutput(-1)),
				Assert.Throws<ArgumentOutOfRangeException>(() => transaction.GetOwnedOutput(transaction.OwnedOutputCount)),
				Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidWalletFactsWireV1UntrustedStructuralTransactionView(response, -1)),
				Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidWalletFactsWireV1UntrustedStructuralInputView(response, 0, -1)),
				Assert.Throws<ArgumentOutOfRangeException>(() => new LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView(response, 0, -1)),
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

		Type owner = typeof(LiquidWalletFactsWireV1UntrustedStructuralResponse);
		Type decoder = Assert.IsAssignableFrom<Type>(owner.GetNestedType("Decoder", BindingFlags.NonPublic));
		Type[] ownerSubtree = TypeAndNestedTypes(owner).ToArray();
		Type responseHeader = Assert.IsAssignableFrom<Type>(decoder.GetNestedType("ResponseHeader", BindingFlags.NonPublic));
		Type wireReader = Assert.IsAssignableFrom<Type>(decoder.GetNestedType("WireReader", BindingFlags.NonPublic));
		Type[] exactOwnerSubtree =
		[
			owner,
			decoder,
			responseHeader,
			wireReader,
			typeof(LiquidWalletFactsWireV1UntrustedStructuralTransactionView),
			typeof(LiquidWalletFactsWireV1UntrustedStructuralInputView),
			typeof(LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView),
		];
		Assert.Equal(
			exactOwnerSubtree.Select(type => type.FullName).Order(StringComparer.Ordinal),
			ownerSubtree.Select(type => type.FullName).Order(StringComparer.Ordinal));
		Assert.Equal(
			"a60d36f0e849016e40ed323129c6af705bdf69a4760b8a1dbe12fc53eef484d8",
			ComputeStructuralResponseSurfaceFingerprint(ownerSubtree));
		Assert.False(owner.IsPublic);
		Assert.True(owner.IsNotPublic);
		Assert.True(owner.IsSealed);
		Assert.False(owner.IsAbstract);
		Assert.True(decoder.IsNestedPrivate);
		Assert.True(decoder.IsAbstract && decoder.IsSealed);
		Assert.True(responseHeader.IsNestedPrivate && responseHeader.IsValueType && responseHeader.IsSealed);
		Assert.True(wireReader.IsNestedPrivate && wireReader.IsValueType && wireReader.IsByRefLike && wireReader.IsSealed);
		Assert.All(
			new[]
			{
				typeof(LiquidWalletFactsWireV1UntrustedStructuralTransactionView),
				typeof(LiquidWalletFactsWireV1UntrustedStructuralInputView),
				typeof(LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView),
			},
			type => Assert.True(type.IsNestedAssembly && type.IsClass && type.IsSealed));
		Assert.Equal(new[] { typeof(IDisposable) }, owner.GetInterfaces());
		Assert.Empty(typeof(LiquidWalletFactsWireV1UntrustedStructuralTransactionView).GetInterfaces());
		Assert.Empty(typeof(LiquidWalletFactsWireV1UntrustedStructuralInputView).GetInterfaces());
		Assert.Empty(typeof(LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView).GetInterfaces());
		Assert.True(IsForbiddenCapability(typeof(LiquidWalletFactsWireV1UnpreparedRequestFrame)));
		Assert.True(IsForbiddenCapability(typeof(TimeProvider)));
		Assert.True(IsForbiddenCapability(typeof(System.Security.Cryptography.RandomNumberGenerator)));
		Assert.True(IsForbiddenCapability(typeof(System.IO.StreamReader)));
		Assert.True(IsForbiddenCapability(typeof(System.IO.MemoryMappedFiles.MemoryMappedFile)));
		Assert.True(IsForbiddenCapability(typeof(WalletWasabi.Wallets.Wallet)));
		Assert.True(IsForbiddenCapability(typeof(WalletWasabi.Liquid.Wallet.LiquidOwnedOutput)));
		Assert.True(IsForbiddenCapability(typeof(WalletWasabi.Liquid.Wallet.LiquidSpendKeyReference)));
		Assert.True(IsForbiddenCapability(typeof(NBitcoin.Key)));
		Assert.True(IsForbiddenCapability(typeof(NBitcoin.Secp256k1.ECPrivKey)));
		Assert.True(IsForbiddenCapability(typeof(IServiceProvider)));
		Assert.True(IsForbiddenCapability(typeof(System.Text.Json.JsonSerializer)));
		Assert.True(IsForbiddenCapability(typeof(Newtonsoft.Json.JsonConvert)));
		Assert.True(IsForbiddenCapability(typeof(System.Linq.Expressions.Expression)));
		Assert.True(IsForbiddenCapability(typeof(Microsoft.CSharp.RuntimeBinder.Binder)));
		Assert.True(IsForbiddenCapability(typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider)));
		Assert.True(IsForbiddenCapability(typeof(System.Runtime.CompilerServices.CallSite)));
		Assert.True(IsForbiddenCapability(typeof(System.Runtime.CompilerServices.UnsafeAccessorAttribute)));
		Assert.True(IsForbiddenCapability(typeof(System.Runtime.CompilerServices.UnsafeAccessorKind)));
		Assert.False(IsAllowedStructuralResponseMetadataType(typeof(System.Runtime.CompilerServices.UnsafeAccessorAttribute)));
		Assert.True(IsForbiddenCapability(typeof(System.Security.Cryptography.ECDsa)));
		Assert.True(IsForbiddenCapability(typeof(NBitcoin.RandomUtils)));
		Assert.True(IsForbiddenCapability(typeof(Guid)));
		Assert.True(IsForbiddenCapability(typeof(System.Threading.Timer)));
		Assert.True(IsForbiddenCapability(typeof(Activator)));
		Assert.True(IsForbiddenMember(typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!));
		Assert.True(IsForbiddenMember(typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.GetUninitializedObject))!));
		Assert.True(IsForbiddenMember(typeof(NBitcoin.PSBT).GetMethod("Finalize", Type.EmptyTypes)!));
		Assert.True(IsForbiddenMember(typeof(NBitcoin.TransactionBuilder).GetMethods().First(method => method.Name == "SignPSBT")));
		Assert.True(IsForbiddenMember(typeof(System.Threading.Monitor).GetMethod("Wait", new[] { typeof(object), typeof(int) })!));
		Assert.False(IsForbiddenMember(typeof(System.Threading.Monitor).GetMethods().First(method => method.Name == "Enter")));
		Assert.True(IsForbiddenMember(typeof(GC).GetMethod(nameof(GC.Collect), Type.EmptyTypes)!));
		Assert.False(IsForbiddenMember(typeof(GC).GetMethods().First(method => method.Name == nameof(GC.AllocateUninitializedArray))));
		Assert.True(IsAllowedStructuralResponseCapability(typeof(ReadOnlySpan<byte>)));
		Assert.True(IsAllowedStructuralResponseCapability(typeof(System.Security.Cryptography.CryptographicOperations)));
		Assert.True(IsAllowedStructuralResponseCapability(typeof(NBitcoin.PubKey)));
		Assert.False(IsAllowedStructuralResponseCapability(typeof(WalletWasabi.Stores.TransactionSqliteStorage)));
		Assert.False(IsAllowedStructuralResponseCapability(typeof(WalletWasabi.BundledApps.ProcessAsync)));
		Assert.False(IsAllowedStructuralResponseCapability(typeof(NBitcoin.RPC.RPCClient)));
		Assert.False(IsAllowedStructuralResponseCapability(typeof(WalletWasabi.BitcoinRpc.RpcClientBase)));
		BindingFlags declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		AssertNonPrivateDeclaredMemberNames(typeof(LiquidWalletFactsWireErrorCode));
		AssertNonPrivateDeclaredMemberNames(typeof(LiquidWalletFactsWireErrorCodeExtensions), "GetMessage");
		AssertNonPrivateDeclaredMemberNames(typeof(LiquidWalletFactsWireV1Branch));
		AssertNonPrivateDeclaredMemberNames(
			owner,
			"Dispose",
			"GetSourceEpoch",
			"GetTransaction",
			"TryDecodeUntrustedStructuralResponse",
			"ToString",
			"get_IsEmpty",
			"get_OwnedOutputCount",
			"get_TransactionCount");
		AssertNonPrivateDeclaredMemberNames(decoder, "TryDecodeUntrustedStructuralResponse");
		MethodInfo outerEntryPoint = Assert.Single(
			owner.GetMethods(declared),
			method => method.Name == "TryDecodeUntrustedStructuralResponse");
		AssertExactDecodeSignature(outerEntryPoint);
		AssertNonPrivateDeclaredMemberNames(
			typeof(LiquidWalletFactsWireV1UntrustedStructuralTransactionView),
			".ctor",
			"GetInput",
			"GetOwnedOutput",
			"GetTransactionIdConsensusBytes",
			"GetTransactionWitnessBinding",
			"ToString",
			"get_InputCount",
			"get_OwnedOutputCount");
		AssertNonPrivateDeclaredMemberNames(
			typeof(LiquidWalletFactsWireV1UntrustedStructuralInputView),
			".ctor",
			"GetPreviousTransactionIdConsensusBytes",
			"ToString",
			"get_PreviousOutputIndex");
		AssertNonPrivateDeclaredMemberNames(
			typeof(LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView),
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
				typeof(LiquidWalletFactsWireV1UntrustedStructuralTransactionView),
				typeof(LiquidWalletFactsWireV1UntrustedStructuralInputView),
				typeof(LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView),
			},
			type => Assert.All(type.GetConstructors(declared), constructor => Assert.True(constructor.IsAssembly)));

		Type[] capabilitySurface =
		[
			.. ownerSubtree,
			typeof(LiquidWalletFactsWireErrorCode),
			typeof(LiquidWalletFactsWireErrorCodeExtensions),
			typeof(LiquidWalletFactsWireV1Branch),
		];
		AssertNoForbiddenCapabilities(capabilitySurface);

		ulong[] literalValues = decoder
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

	[Fact]
	public void ProductionAssemblyQuarantinesConstructionAndInboundReferences()
	{
		Type owner = typeof(LiquidWalletFactsWireV1UntrustedStructuralResponse);
		Type decoder = Assert.IsAssignableFrom<Type>(owner.GetNestedType("Decoder", BindingFlags.NonPublic));
		Type[] targetTypes =
		[
			owner,
			typeof(LiquidWalletFactsWireV1UntrustedStructuralTransactionView),
			typeof(LiquidWalletFactsWireV1UntrustedStructuralInputView),
			typeof(LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView),
		];
		var targetSet = targetTypes.ToHashSet();
		var ownerSubtree = TypeAndNestedTypes(owner).ToHashSet();
		// NATIVE-WALLET-FACTS-OBSERVATION-FFI-001 requires this observer to be the single
		// authorized consumer of the landed WLFV decoder; no other decode path is allowed.
		var allowedConsumers = new HashSet<Type>
		{
			typeof(WalletWasabi.Liquid.WalletFacts.LiquidWalletNativeFactsObserver),
		};
		ConstructorInfo ownerConstructor = Assert.Single(
			owner.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
		Assert.True(ownerConstructor.IsPrivate);
		Assert.Equal(
			new[] { typeof(byte[]), typeof(int[]) },
			ownerConstructor.GetParameters().Select(parameter => parameter.ParameterType));

		Type[] productionTypes = owner.Assembly.GetTypes();
		var constructorCalls = productionTypes
			.SelectMany(GetDeclaredMethodsAndConstructors)
			.SelectMany(method => ReferencedMembersWithOpcodes(method).Select(reference => (Method: method, reference.Opcode, reference.Member)))
			.Where(reference => reference.Opcode == OpCodes.Newobj && reference.Member == ownerConstructor)
			.ToArray();
		var constructorCall = Assert.Single(constructorCalls);
		MethodInfo decoderEntryPoint = Assert.Single(
			decoder.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly),
			method => method.Name == "TryDecodeUntrustedStructuralResponse");
		AssertExactDecodeSignature(decoderEntryPoint);
		Assert.Equal(decoderEntryPoint, constructorCall.Method);

		foreach (Type type in productionTypes.Where(type => !ownerSubtree.Contains(type) && !allowedConsumers.Contains(type)))
		{
			Assert.False(
				TypeDefinitionReferencesTargets(type, targetSet),
				$"Production type outside the response subtree references a quarantined type: {type.FullName}");
			foreach ((string location, CustomAttributeData attribute) in EnumerateCustomAttributes(type))
			{
				Assert.False(
					CustomAttributeReferences(attribute, targetSet.Contains),
					$"Production custom attribute references a quarantined type at {location}: {attribute.AttributeType}");
			}
		}

		foreach ((string location, CustomAttributeData attribute) in EnumerateAssemblyAndModuleCustomAttributes(owner.Assembly))
		{
			Assert.False(
				CustomAttributeReferences(attribute, targetSet.Contains),
				$"Production custom attribute references a quarantined type at {location}: {attribute.AttributeType}");
			Assert.False(
				CustomAttributeReferences(
					attribute,
					candidate => !IsAllowedAssemblyMetadataDependency(candidate)),
				$"Production assembly or module attribute references a forbidden capability at {location}: {attribute.AttributeType}");
		}
		CustomAttributeData friendAssemblyAttribute = Assert.Single(
			owner.Assembly.GetCustomAttributesData(),
			attribute => attribute.AttributeType == typeof(InternalsVisibleToAttribute));
		Assert.Equal(
			new[] { typeof(string) },
			friendAssemblyAttribute.Constructor.GetParameters().Select(parameter => parameter.ParameterType));
		CustomAttributeTypedArgument friendAssemblyArgument = Assert.Single(friendAssemblyAttribute.ConstructorArguments);
		Assert.Equal(typeof(string), friendAssemblyArgument.ArgumentType);
		Assert.Empty(friendAssemblyAttribute.NamedArguments);
		Assert.True(IsExactFriendAssemblyInventory([Assert.IsType<string>(friendAssemblyArgument.Value)]));

		AssertMetadataFixtureReferencesTargets(typeof(TargetScalarAttributeFixture), targetSet.Contains);
		AssertMetadataFixtureReferencesTargets(typeof(TargetArrayAttributeFixture), targetSet.Contains);
		AssertMetadataFixtureReferencesTargets(typeof(TargetGenericAttributeFixture), targetSet.Contains);
		AssertMetadataFixtureReferencesTargets(typeof(ForbiddenWalletScalarAttributeFixture), IsForbiddenCapability);
		AssertMetadataFixtureReferencesTargets(typeof(ForbiddenNativeLoaderScalarAttributeFixture), IsForbiddenCapability);
		AssertMetadataFixtureReferencesTargets(typeof(ForbiddenWalletArrayAttributeFixture), IsForbiddenCapability);
		AssertMetadataFixtureReferencesTargets(typeof(ForbiddenNativeLoaderArrayAttributeFixture), IsForbiddenCapability);
		AssertMetadataFixtureReferencesTargets(typeof(ForbiddenWalletGenericAttributeFixture), IsForbiddenCapability);
		AssertMetadataFixtureReferencesTargets(typeof(ForbiddenNativeLoaderGenericAttributeFixture), IsForbiddenCapability);
		AssertMetadataFixtureReferencesTargets(
			typeof(DisallowedDependencyScalarAttributeFixture),
			type => !IsAllowedStructuralResponseMetadataType(type));
		Assert.False(IsAllowedAssemblyMetadataDependency(typeof(WalletWasabi.Stores.TransactionSqliteStorage)));
		Assert.False(IsAllowedAssemblyMetadataDependency(typeof(WalletWasabi.BundledApps.ProcessAsync)));
		Assert.False(IsAllowedAssemblyMetadataDependency(typeof(NBitcoin.RPC.RPCClient)));
		AssertMetadataFixtureReferencesTargets(
			typeof(DisallowedDependencyScalarAttributeFixture),
			type => type == typeof(WalletWasabi.Stores.TransactionSqliteStorage));

		string productionNamespace = owner.Namespace!;
		string[] oldTypeNames =
		[
			$"{productionNamespace}.LiquidWalletFactsWireV1ResponseCodec",
			$"{productionNamespace}.LiquidWalletFactsWireV1Response",
			$"{productionNamespace}.LiquidWalletFactsWireV1Response+LiquidWalletFactsWireV1TransactionView",
			$"{productionNamespace}.LiquidWalletFactsWireV1Response+LiquidWalletFactsWireV1InputView",
			$"{productionNamespace}.LiquidWalletFactsWireV1Response+LiquidWalletFactsWireV1OwnedOutputView",
		];
		Assert.All(oldTypeNames, oldTypeName => Assert.Null(owner.Assembly.GetType(oldTypeName, throwOnError: false, ignoreCase: false)));

		AssertExactPartialSourceDeclarations();
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
				LiquidWalletFactsWireV1UntrustedStructuralResponse? response = null;
				try
				{
					bool accepted = LiquidWalletFactsWireV1UntrustedStructuralResponse.TryDecodeUntrustedStructuralResponse(
						frame,
						expectedSource,
						out response,
						out LiquidWalletFactsWireErrorCode errorCode);
					if (accepted)
					{
						acceptedCount++;
						Assert.Equal(LiquidWalletFactsWireErrorCode.None, errorCode);
						LiquidWalletFactsWireV1UntrustedStructuralResponse ownedResponse = Assert.IsType<LiquidWalletFactsWireV1UntrustedStructuralResponse>(response);
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

	private static void AssertExactDecodeSignature(MethodInfo method)
	{
		Assert.Equal(typeof(bool), method.ReturnType);
		ParameterInfo[] parameters = method.GetParameters();
		Assert.Equal(
			new[]
			{
				typeof(ReadOnlySpan<byte>),
				typeof(ReadOnlySpan<byte>),
				typeof(LiquidWalletFactsWireV1UntrustedStructuralResponse).MakeByRefType(),
				typeof(LiquidWalletFactsWireErrorCode).MakeByRefType(),
			},
			parameters.Select(parameter => parameter.ParameterType));
		Assert.False(parameters[0].IsOut);
		Assert.False(parameters[1].IsOut);
		Assert.True(parameters[2].IsOut);
		Assert.True(parameters[3].IsOut);
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

	private static string ComputeStructuralResponseSurfaceFingerprint(IEnumerable<Type> types)
	{
		const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		var lines = new List<string>();
		foreach (Type type in types.OrderBy(TypeName, StringComparer.Ordinal))
		{
			lines.Add($"T|{TypeName(type)}|{(int)type.Attributes}|{TypeName(type.BaseType)}|{string.Join(',', type.GetInterfaces().Select(TypeName).Order(StringComparer.Ordinal))}");
			foreach (FieldInfo field in type.GetFields(Declared).OrderBy(field => field.Name, StringComparer.Ordinal))
			{
				string literal = field.IsLiteral
					? Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) ?? "<null>"
					: "-";
				lines.Add($"F|{TypeName(type)}|{field.Name}|{TypeName(field.FieldType)}|{(int)field.Attributes}|{literal}");
			}
			foreach (PropertyInfo property in type.GetProperties(Declared).OrderBy(property => property.Name, StringComparer.Ordinal))
			{
				lines.Add($"P|{TypeName(type)}|{property.Name}|{TypeName(property.PropertyType)}|{(int)property.Attributes}|{MethodShape(property.GetMethod)}|{MethodShape(property.SetMethod)}|{string.Join(',', property.GetIndexParameters().Select(ParameterShape))}");
			}
			foreach (EventInfo @event in type.GetEvents(Declared).OrderBy(@event => @event.Name, StringComparer.Ordinal))
			{
				lines.Add($"E|{TypeName(type)}|{@event.Name}|{TypeName(@event.EventHandlerType)}|{(int)@event.Attributes}|{MethodShape(@event.AddMethod)}|{MethodShape(@event.RemoveMethod)}");
			}
			foreach (MethodBase method in GetDeclaredMethodsAndConstructors(type)
				.OrderBy(method => method.Name, StringComparer.Ordinal)
				.ThenBy(MethodShape, StringComparer.Ordinal))
			{
				lines.Add($"M|{TypeName(type)}|{MethodShape(method)}");
			}
		}

		byte[] digest = System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)));
		return Convert.ToHexString(digest).ToLowerInvariant();
	}

	private static string MethodShape(MethodBase? method)
	{
		if (method is null)
		{
			return "-";
		}

		string returnType = method is MethodInfo methodInfo ? TypeName(methodInfo.ReturnType) : "<ctor>";
		int genericArity = method is MethodInfo genericMethod ? genericMethod.GetGenericArguments().Length : 0;
		return $"{method.Name}|{returnType}|{(int)method.Attributes}|{(int)method.GetMethodImplementationFlags()}|{genericArity}|{string.Join(',', method.GetParameters().Select(ParameterShape))}";
	}

	private static string ParameterShape(ParameterInfo parameter) =>
		$"{parameter.Position}:{TypeName(parameter.ParameterType)}:{(int)parameter.Attributes}";

	private static string TypeName(Type? type)
	{
		if (type is null)
		{
			return "-";
		}
		if (type.IsByRef)
		{
			return $"{TypeName(type.GetElementType())}&";
		}
		if (type.IsPointer)
		{
			return $"{TypeName(type.GetElementType())}*";
		}
		if (type.IsArray)
		{
			return $"{TypeName(type.GetElementType())}[{new string(',', type.GetArrayRank() - 1)}]";
		}
		if (type.IsGenericParameter)
		{
			return $"!{type.GenericParameterPosition}:{type.Name}";
		}
		if (type.IsGenericType)
		{
			return $"{type.GetGenericTypeDefinition().FullName}<{string.Join(',', type.GetGenericArguments().Select(TypeName))}>";
		}
		return type.FullName ?? type.Name;
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
		if (typeof(IServiceProvider).IsAssignableFrom(type))
		{
			return true;
		}
		if (name is
			"System.Security.Cryptography.CryptographicOperations" or
			"System.Runtime.InteropServices.InAttribute" or
			"System.Runtime.InteropServices.OutAttribute")
		{
			return false;
		}
		return name is
			"System.Activator" or
			"System.Diagnostics.Process" or
			"System.Environment" or
			"System.Guid" or
			"System.IServiceProvider" or
			"System.Random" or
			"System.DateTime" or
			"System.DateTimeOffset" or
			"System.TimeProvider" or
			"System.Threading.PeriodicTimer" or
			"System.Threading.Timer" or
			"System.Timers.Timer" or
			"System.Runtime.CompilerServices.UnsafeAccessorAttribute" or
			"System.Runtime.CompilerServices.UnsafeAccessorKind" or
			"NBitcoin.PSBT" or
			"NBitcoin.RandomUtils" or
			"NBitcoin.Key" or
			"NBitcoin.Secp256k1.ECPrivKey" or
			"System.Security.Cryptography.RandomNumberGenerator" or
			"System.Diagnostics.Stopwatch" ||
			name.StartsWith("System.Net.", StringComparison.Ordinal) ||
			name.StartsWith("System.IO.", StringComparison.Ordinal) ||
			name.StartsWith("System.Linq.Expressions.", StringComparison.Ordinal) ||
			name.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
			name.StartsWith("System.Runtime.CompilerServices.CallSite", StringComparison.Ordinal) ||
			name.StartsWith("System.Runtime.InteropServices.", StringComparison.Ordinal) ||
			name.StartsWith("System.Runtime.Loader.", StringComparison.Ordinal) ||
			name.StartsWith("System.Runtime.Serialization.", StringComparison.Ordinal) ||
			name.StartsWith("System.Security.Cryptography.", StringComparison.Ordinal) ||
			name.StartsWith("System.Text.Json.", StringComparison.Ordinal) ||
			name.StartsWith("System.Xml.", StringComparison.Ordinal) ||
			name.StartsWith("Microsoft.CSharp.RuntimeBinder.", StringComparison.Ordinal) ||
			name.StartsWith("Microsoft.Extensions.DependencyInjection.", StringComparison.Ordinal) ||
			name.StartsWith("Newtonsoft.Json.", StringComparison.Ordinal) ||
			name.StartsWith("WalletWasabi.Blockchain.Keys.", StringComparison.Ordinal) ||
			name.StartsWith("WalletWasabi.Liquid.Interop.", StringComparison.Ordinal) ||
			name.StartsWith("WalletWasabi.Liquid.Rpc.", StringComparison.Ordinal) ||
			name.StartsWith("WalletWasabi.Liquid.Wallet.", StringComparison.Ordinal) ||
			name.StartsWith("WalletWasabi.Wallets.", StringComparison.Ordinal) ||
			name.StartsWith("WalletWasabi.WabiSabi.", StringComparison.Ordinal) ||
			name.Contains("ElementsNode", StringComparison.Ordinal) ||
			name.Contains("NetworkManifest", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletFactsWireV1StructuralRequest", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletFactsWireV1UnpreparedRequestFrame", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletState", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletTransactionDelta", StringComparison.Ordinal) ||
			name.Contains("LiquidOwnedOutputObservation", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletTransactionObservation", StringComparison.Ordinal) ||
			name.Contains("LiquidWalletObservationBatch", StringComparison.Ordinal) ||
			name.Contains("CoinJoin", StringComparison.Ordinal) ||
			name.Contains("WabiSabi", StringComparison.Ordinal) ||
			name.Contains("Coordinator", StringComparison.Ordinal) ||
			name.Contains("Sponsor", StringComparison.Ordinal) ||
			name.Contains("Usdt", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Broadcast", StringComparison.Ordinal) ||
			name.Contains("Signer", StringComparison.Ordinal) ||
			name.Contains("Pset", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Mnemonic", StringComparison.Ordinal) ||
			name.Contains("BitcoinSecret", StringComparison.Ordinal) ||
			name.Contains("ExtKey", StringComparison.Ordinal);
	}

	private static bool IsForbiddenMember(MemberInfo member)
	{
		string name = member.Name;
		return member.DeclaringType == typeof(System.Threading.Monitor) && name is not "Enter" and not "Exit" ||
			member.DeclaringType == typeof(GC) && name != nameof(GC.AllocateUninitializedArray) ||
			member.DeclaringType == typeof(RuntimeHelpers) ||
			member.DeclaringType == typeof(object) && name == "MemberwiseClone" ||
			name.StartsWith("Sign", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith("Extract", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Finalize", StringComparison.OrdinalIgnoreCase) ||
			name.Contains("Broadcast", StringComparison.OrdinalIgnoreCase) ||
			IsForbiddenCapability(member.DeclaringType) ||
			MemberReferences(member, IsForbiddenCapability);
	}

	private static void AssertNoForbiddenCapabilities(IReadOnlyCollection<Type> types)
	{
		foreach (Type type in types)
		{
			Type? unexpectedType = null;
			bool referencesUnexpectedType = TypeDefinitionReferences(type, candidate =>
			{
				if (IsAllowedStructuralResponseCapability(candidate))
				{
					return false;
				}

				unexpectedType ??= candidate;
				return true;
			}, IsAllowedStackallocSpanConstructor);
			Assert.False(
				referencesUnexpectedType,
				$"Production type {type.FullName} references dependency outside the structural-response allowlist: {unexpectedType}.");
			Assert.False(
				TypeDefinitionReferences(type, IsForbiddenCapability),
				$"Forbidden production capability referenced by {type.FullName}.");
			MemberInfo? forbiddenMember = GetDeclaredMethodsAndConstructors(type)
				.SelectMany(ReferencedMembersWithOpcodes)
				.Select(reference => reference.Member)
				.FirstOrDefault(IsForbiddenMember);
			Assert.Null(forbiddenMember);
			MemberInfo? unexpectedNBitcoinMember = GetDeclaredMethodsAndConstructors(type)
				.SelectMany(ReferencedMembersWithOpcodes)
				.Select(reference => reference.Member)
				.FirstOrDefault(member =>
					member.DeclaringType?.Namespace?.StartsWith("NBitcoin", StringComparison.Ordinal) == true &&
					!IsAllowedStructuralResponseNBitcoinMember(member));
			Assert.Null(unexpectedNBitcoinMember);

			Type? pointerType = null;
			bool referencesPointer = TypeDefinitionReferences(type, candidate =>
			{
				if (!ContainsPointer(candidate))
				{
					return false;
				}

				pointerType ??= candidate;
				return true;
			}, IsAllowedStackallocSpanConstructor);
			Assert.False(
				referencesPointer,
				$"Pointer or function-pointer production capability {pointerType} referenced by {type.FullName}.");

			foreach (MethodBase method in GetDeclaredMethodsAndConstructors(type))
			{
				Assert.False((method.Attributes & MethodAttributes.PinvokeImpl) != 0);
				Assert.False((method.Attributes & MethodAttributes.Abstract) != 0);
				Assert.NotNull(method.GetMethodBody());
				OpCode[] opcodes = ReadOpCodes(method).ToArray();
				int expectedStackallocCount = ExpectedStackallocCount(method);
				Assert.Equal(expectedStackallocCount, opcodes.Count(opcode => opcode == OpCodes.Localloc));
				Assert.Equal(
					expectedStackallocCount,
					ReferencedMembersWithOpcodes(method).Count(reference =>
						reference.Opcode == OpCodes.Newobj &&
						IsAllowedStackallocSpanConstructor(reference.Member)));
				Assert.DoesNotContain(
					opcodes,
					opcode => opcode == OpCodes.Calli || opcode == OpCodes.Cpblk || opcode == OpCodes.Initblk);
				if (method.DeclaringType?.Name == "Decoder" && method.Name == "TryValidateHeader")
				{
					Assert.DoesNotContain(OpCodes.Newarr, opcodes);
				}
				Assert.DoesNotContain(
					method.GetCustomAttributesData(),
					attribute => attribute.AttributeType.FullName == "System.Runtime.InteropServices.DllImportAttribute");
			}

			foreach ((string location, CustomAttributeData attribute) in EnumerateCustomAttributes(type))
			{
				Assert.False(
					CustomAttributeReferences(
						attribute,
						candidate => IsForbiddenCapability(candidate) || !IsAllowedStructuralResponseMetadataType(candidate)),
					$"Production attribute dependency is outside the structural-response allowlist at {location}: {attribute.AttributeType}");
			}
		}

	}

	private static int ExpectedStackallocCount(MethodBase method)
	{
		Type owner = typeof(LiquidWalletFactsWireV1UntrustedStructuralResponse);
		if (method.DeclaringType?.DeclaringType != owner || method.DeclaringType.Name != "Decoder")
		{
			return 0;
		}

		return method.Name switch
		{
			"TryDecodeUntrustedStructuralResponse" => 2,
			"TryValidateLayout" => 1,
			_ => 0,
		};
	}

	private static bool IsAllowedStackallocSpanConstructor(MemberInfo member)
	{
		if (member is not ConstructorInfo constructor || constructor.DeclaringType != typeof(Span<byte>))
		{
			return false;
		}

		ParameterInfo[] parameters = constructor.GetParameters();
		return parameters.Length == 2 &&
			parameters[0].ParameterType.IsPointer &&
			parameters[0].ParameterType.GetElementType() == typeof(void) &&
			parameters[1].ParameterType == typeof(int);
	}

	private static bool IsAllowedStructuralResponseNBitcoinMember(MemberInfo member) =>
		member.DeclaringType == typeof(NBitcoin.PubKey) &&
			(member is ConstructorInfo || member.Name is "get_IsCompressed" or "get_WitHash") ||
		member.DeclaringType == typeof(NBitcoin.WitKeyId) && member.Name == "get_ScriptPubKey" ||
		member.DeclaringType == typeof(NBitcoin.Script) && member.Name == "ToBytes";

	private static bool IsAllowedStructuralResponseCapability(Type type)
	{
		if (type.IsPointer || type.IsFunctionPointer)
		{
			return false;
		}
		if (type.IsGenericParameter)
		{
			return type.GetGenericParameterConstraints().All(IsAllowedStructuralResponseCapability);
		}
		if (type.HasElementType)
		{
			return IsAllowedStructuralResponseCapability(type.GetElementType()!);
		}
		if (type.IsGenericType)
		{
			Type definition = type.GetGenericTypeDefinition();
			bool allowedDefinition = definition == typeof(ReadOnlySpan<>) || definition == typeof(Span<>);
			return allowedDefinition &&
				(type.IsGenericTypeDefinition || type.GetGenericArguments().All(IsAllowedStructuralResponseCapability));
		}

		Type owner = typeof(LiquidWalletFactsWireV1UntrustedStructuralResponse);
		for (Type? containingType = type; containingType is not null; containingType = containingType.DeclaringType)
		{
			if (containingType == owner)
			{
				return true;
			}
		}

		return type == typeof(bool) ||
			type == typeof(byte) ||
			type == typeof(int) ||
			type == typeof(uint) ||
			type == typeof(ulong) ||
			type == typeof(ushort) ||
			type == typeof(LiquidWalletFactsWireErrorCode) ||
			type == typeof(LiquidWalletFactsWireV1Branch) ||
			type == typeof(ArgumentException) ||
			type == typeof(ArgumentNullException) ||
			type == typeof(ArgumentOutOfRangeException) ||
			type == typeof(Array) ||
			type == typeof(System.Buffers.Binary.BinaryPrimitives) ||
			type == typeof(Enum) ||
			type == typeof(Exception) ||
			type == typeof(FormatException) ||
			type == typeof(GC) ||
			type == typeof(IComparable) ||
			type == typeof(IConvertible) ||
			type == typeof(IDisposable) ||
			type == typeof(IFormattable) ||
			type == typeof(ISpanFormattable) ||
			type == typeof(Index) ||
			type == typeof(InvalidOperationException) ||
			type == typeof(Math) ||
			type == typeof(MemoryExtensions) ||
			type == typeof(object) ||
			type == typeof(ObjectDisposedException) ||
			type == typeof(Range) ||
			type == typeof(System.Security.Cryptography.CryptographicOperations) ||
			type == typeof(string) ||
			type == typeof(System.Threading.Monitor) ||
			type == typeof(ValueType) ||
			type == typeof(void) ||
			type == typeof(NBitcoin.PubKey) ||
			type == typeof(NBitcoin.Script) ||
			type == typeof(NBitcoin.WitKeyId);
	}

	private static bool IsAllowedStructuralResponseMetadataType(Type type)
	{
		if (IsAllowedStructuralResponseCapability(type))
		{
			return true;
		}
		if (type.Assembly != typeof(object).Assembly)
		{
			return false;
		}

		string name = type.FullName ?? type.Name;
		return name is
			"System.Diagnostics.DebuggerBrowsableAttribute" or
			"System.Diagnostics.DebuggerBrowsableState" or
			"System.ObsoleteAttribute" or
			"System.Runtime.CompilerServices.CompilerGeneratedAttribute" or
			"System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute" or
			"System.Runtime.CompilerServices.ExtensionAttribute" or
			"System.Runtime.CompilerServices.IsByRefLikeAttribute" or
			"System.Runtime.CompilerServices.IsReadOnlyAttribute" or
			"System.Runtime.CompilerServices.NullableAttribute" or
			"System.Runtime.CompilerServices.NullableContextAttribute" or
			"System.Runtime.CompilerServices.RefSafetyRulesAttribute" or
			"System.Runtime.InteropServices.InAttribute" or
			"System.Runtime.InteropServices.OutAttribute";
	}

	private static bool IsAllowedAssemblyMetadataDependency(Type type)
	{
		if (type.HasElementType)
		{
			return IsAllowedAssemblyMetadataDependency(type.GetElementType()!);
		}
		if (type == typeof(bool) || type == typeof(byte) || type == typeof(int) || type == typeof(string))
		{
			return true;
		}
		if (type.Assembly != typeof(object).Assembly)
		{
			return false;
		}

		string name = type.FullName ?? type.Name;
		return name is
			"System.Diagnostics.DebuggableAttribute" or
			"System.Diagnostics.DebuggableAttribute+DebuggingModes" or
			"System.Reflection.AssemblyCompanyAttribute" or
			"System.Reflection.AssemblyConfigurationAttribute" or
			"System.Reflection.AssemblyFileVersionAttribute" or
			"System.Reflection.AssemblyInformationalVersionAttribute" or
			"System.Reflection.AssemblyMetadataAttribute" or
			"System.Reflection.AssemblyProductAttribute" or
			"System.Reflection.AssemblyTitleAttribute" or
			"System.Runtime.CompilerServices.CompilationRelaxationsAttribute" or
			"System.Runtime.CompilerServices.ExtensionAttribute" or
			"System.Runtime.CompilerServices.InternalsVisibleToAttribute" or
			"System.Runtime.CompilerServices.RefSafetyRulesAttribute" or
			"System.Runtime.CompilerServices.RuntimeCompatibilityAttribute" or
			"System.Runtime.Versioning.TargetFrameworkAttribute" or
			"System.Security.UnverifiableCodeAttribute";
	}

	private static IEnumerable<MethodBase> GetDeclaredMethodsAndConstructors(Type type)
	{
		const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		return type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared));
	}

	private static bool TypeDefinitionReferencesTargets(Type type, IReadOnlySet<Type> targets) =>
		TypeDefinitionReferences(type, targets.Contains);

	private static bool TypeDefinitionReferences(
		Type type,
		Func<Type, bool> predicate,
		Func<MemberInfo, bool>? allowedReferencedMember = null)
	{
		if (TypeReferences(type.BaseType, predicate) ||
			type.GetInterfaces().Any(candidate => TypeReferences(candidate, predicate)) ||
			type.GetGenericArguments().Any(candidate => TypeReferences(candidate, predicate)))
		{
			return true;
		}

		const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		if (type.GetFields(Declared).Any(field => TypeReferences(field.FieldType, predicate)) ||
			type.GetProperties(Declared).Any(property =>
				TypeReferences(property.PropertyType, predicate) ||
				property.GetIndexParameters().Any(parameter => TypeReferences(parameter.ParameterType, predicate))) ||
			type.GetEvents(Declared).Any(@event => TypeReferences(@event.EventHandlerType, predicate)))
		{
			return true;
		}

		foreach (MethodBase method in GetDeclaredMethodsAndConstructors(type))
		{
			if (method is MethodInfo methodInfo && TypeReferences(methodInfo.ReturnType, predicate))
			{
				return true;
			}
			if (method.GetParameters().Any(parameter => TypeReferences(parameter.ParameterType, predicate)) ||
				method is MethodInfo genericMethod &&
				genericMethod.GetGenericArguments().Any(candidate => TypeReferences(candidate, predicate)))
			{
				return true;
			}

			MethodBody? body = method.GetMethodBody();
			if (body is not null &&
				(body.LocalVariables.Any(local => TypeReferences(local.LocalType, predicate)) ||
				 body.ExceptionHandlingClauses.Any(clause =>
					 clause.Flags == ExceptionHandlingClauseOptions.Clause &&
					 TypeReferences(clause.CatchType, predicate))))
			{
				return true;
			}

			if (ReferencedMembersWithOpcodes(method).Any(reference =>
				!(allowedReferencedMember?.Invoke(reference.Member) ?? false) &&
				MemberReferences(reference.Member, predicate)))
			{
				return true;
			}
		}

		return false;
	}
	private static bool MemberReferences(MemberInfo member, Func<Type, bool> predicate)
	{
		if (member is Type referencedType && TypeReferences(referencedType, predicate))
		{
			return true;
		}
		if (TypeReferences(member.DeclaringType, predicate))
		{
			return true;
		}

		return member switch
		{
			FieldInfo field => TypeReferences(field.FieldType, predicate),
			PropertyInfo property =>
				TypeReferences(property.PropertyType, predicate) ||
				property.GetIndexParameters().Any(parameter => TypeReferences(parameter.ParameterType, predicate)),
			EventInfo @event => TypeReferences(@event.EventHandlerType, predicate),
			MethodInfo method =>
				TypeReferences(method.ReturnType, predicate) ||
				method.GetParameters().Any(parameter => TypeReferences(parameter.ParameterType, predicate)) ||
				method.GetGenericArguments().Any(argument => TypeReferences(argument, predicate)),
			ConstructorInfo constructor =>
				constructor.GetParameters().Any(parameter => TypeReferences(parameter.ParameterType, predicate)),
			_ => false,
		};
	}

	private static bool TypeReferences(Type? type, Func<Type, bool> predicate) =>
		TypeReferences(type, predicate, new HashSet<Type>());

	private static bool TypeReferences(Type? type, Func<Type, bool> predicate, ISet<Type> visited)
	{
		if (type is null || !visited.Add(type))
		{
			return false;
		}
		if (predicate(type))
		{
			return true;
		}
		if (type.IsFunctionPointer &&
			(TypeReferences(type.GetFunctionPointerReturnType(), predicate, visited) ||
			 type.GetFunctionPointerParameterTypes().Any(parameter => TypeReferences(parameter, predicate, visited)) ||
			 type.GetFunctionPointerCallingConventions().Any(convention => TypeReferences(convention, predicate, visited))))
		{
			return true;
		}
		if (type.HasElementType && TypeReferences(type.GetElementType(), predicate, visited))
		{
			return true;
		}
		if (type.IsGenericType &&
			(TypeReferences(type.GetGenericTypeDefinition(), predicate, visited) ||
			 type.GetGenericArguments().Any(argument => TypeReferences(argument, predicate, visited))))
		{
			return true;
		}
		return type.IsGenericParameter &&
			type.GetGenericParameterConstraints().Any(constraint => TypeReferences(constraint, predicate, visited));
	}

	private static IEnumerable<(string Location, CustomAttributeData Attribute)> EnumerateAssemblyAndModuleCustomAttributes(Assembly assembly)
	{
		foreach (CustomAttributeData attribute in assembly.GetCustomAttributesData())
		{
			yield return ("assembly", attribute);
		}
		foreach (Module module in assembly.GetModules())
		{
			foreach (CustomAttributeData attribute in module.GetCustomAttributesData())
			{
				yield return ($"module {module.Name}", attribute);
			}
		}
	}

	private static IEnumerable<(string Location, CustomAttributeData Attribute)> EnumerateCustomAttributes(Type type)
	{
		foreach (CustomAttributeData attribute in type.GetCustomAttributesData())
		{
			yield return ($"type {type.FullName}", attribute);
		}
		foreach (Type genericParameter in type.GetGenericArguments().Where(argument => argument.IsGenericParameter))
		{
			foreach (CustomAttributeData attribute in genericParameter.GetCustomAttributesData())
			{
				yield return ($"type generic parameter {type.FullName}.{genericParameter.Name}", attribute);
			}
		}

		const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
		foreach (MemberInfo member in type.GetFields(Declared).Cast<MemberInfo>()
			.Concat(type.GetProperties(Declared))
			.Concat(type.GetEvents(Declared)))
		{
			foreach (CustomAttributeData attribute in member.GetCustomAttributesData())
			{
				yield return ($"member {type.FullName}.{member.Name}", attribute);
			}
			if (member is PropertyInfo property)
			{
				foreach (ParameterInfo parameter in property.GetIndexParameters())
				{
					foreach (CustomAttributeData attribute in parameter.GetCustomAttributesData())
					{
						yield return ($"property parameter {type.FullName}.{member.Name}.{parameter.Position}", attribute);
					}
				}
			}
		}

		foreach (MethodBase method in GetDeclaredMethodsAndConstructors(type))
		{
			foreach (CustomAttributeData attribute in method.GetCustomAttributesData())
			{
				yield return ($"method {type.FullName}.{method.Name}", attribute);
			}
			IEnumerable<Type> genericParameters = method is MethodInfo genericMethod
				? genericMethod.GetGenericArguments().Where(argument => argument.IsGenericParameter)
				: [];
			foreach (Type genericParameter in genericParameters)
			{
				foreach (CustomAttributeData attribute in genericParameter.GetCustomAttributesData())
				{
					yield return ($"method generic parameter {type.FullName}.{method.Name}.{genericParameter.Name}", attribute);
				}
			}
			if (method is MethodInfo methodInfo)
			{
				foreach (CustomAttributeData attribute in methodInfo.ReturnParameter.GetCustomAttributesData())
				{
					yield return ($"return parameter {type.FullName}.{method.Name}", attribute);
				}
			}
			foreach (ParameterInfo parameter in method.GetParameters())
			{
				foreach (CustomAttributeData attribute in parameter.GetCustomAttributesData())
				{
					yield return ($"parameter {type.FullName}.{method.Name}.{parameter.Position}", attribute);
				}
			}
		}
	}

	private static bool CustomAttributeReferences(CustomAttributeData attribute, Func<Type, bool> predicate)
	{
		if (TypeReferences(attribute.AttributeType, predicate) ||
			TypeReferences(attribute.Constructor.DeclaringType, predicate) ||
			attribute.Constructor.GetParameters().Any(parameter => TypeReferences(parameter.ParameterType, predicate)) ||
			attribute.ConstructorArguments.Any(argument => CustomAttributeArgumentReferences(argument, predicate)))
		{
			return true;
		}

		return attribute.NamedArguments.Any(argument =>
			MemberReferences(argument.MemberInfo, predicate) ||
			CustomAttributeArgumentReferences(argument.TypedValue, predicate));
	}

	private static bool CustomAttributeArgumentReferences(CustomAttributeTypedArgument argument, Func<Type, bool> predicate)
	{
		if (TypeReferences(argument.ArgumentType, predicate) ||
			argument.Value is Type typeValue && TypeReferences(typeValue, predicate))
		{
			return true;
		}
		return argument.Value is IEnumerable<CustomAttributeTypedArgument> nestedArguments &&
			nestedArguments.Any(nested => CustomAttributeArgumentReferences(nested, predicate));
	}

	private static void AssertMetadataFixtureReferencesTargets(Type fixture, Func<Type, bool> predicate) =>
		Assert.Contains(fixture.GetCustomAttributesData(), attribute => CustomAttributeReferences(attribute, predicate));

	private static void AssertExactPartialSourceDeclarations([CallerFilePath] string testFilePath = "")
	{
		string repositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "../../../../../"));
		string productionRoot = Path.Combine(repositoryRoot, "WalletWasabi");
		string ownerPath = Path.Combine(productionRoot, "Liquid", "WalletFacts", "Wire", "LiquidWalletFactsWireV1UntrustedStructuralResponse.cs");
		string decoderPath = Path.Combine(productionRoot, "Liquid", "WalletFacts", "Wire", "LiquidWalletFactsWireV1UntrustedStructuralResponse.Decoder.cs");
		Assert.True(File.Exists(ownerPath));
		Assert.True(File.Exists(decoderPath));

		string[] sourcePaths = Directory.EnumerateFiles(productionRoot, "*.cs", SearchOption.AllDirectories)
			.Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
				!path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.ToArray();
		var declarations = sourcePaths
			.SelectMany(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)
				.GetRoot()
				.DescendantNodes()
				.OfType<ClassDeclarationSyntax>()
				.Where(declaration => declaration.Identifier.ValueText == nameof(LiquidWalletFactsWireV1UntrustedStructuralResponse))
				.Select(declaration => (Path: path, Declaration: declaration)))
			.ToArray();
		Assert.Equal(
			new[] { ownerPath, decoderPath }.Order(StringComparer.Ordinal),
			declarations.Select(item => item.Path).Order(StringComparer.Ordinal));
		Assert.All(
			declarations,
			item => Assert.Equal(
				new[] { SyntaxKind.InternalKeyword, SyntaxKind.SealedKeyword, SyntaxKind.PartialKeyword },
				item.Declaration.Modifiers.Select(modifier => modifier.Kind())));

		foreach (string path in new[] { ownerPath, decoderPath })
		{
			CSharpSyntaxNode root = Assert.IsAssignableFrom<CSharpSyntaxNode>(
				CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot());
			Assert.False(ContainsPreprocessorDirective(root));
			Assert.DoesNotContain(
				root.DescendantTokens(),
				token => token.RawKind is (int)SyntaxKind.UnsafeKeyword or (int)SyntaxKind.ExternKeyword);
			Assert.DoesNotContain(
				root.DescendantNodes(),
				node => node is PointerTypeSyntax or
					FunctionPointerTypeSyntax or
					ImplicitStackAllocArrayCreationExpressionSyntax or
					FixedStatementSyntax);
		}

		var ownerRoot = CSharpSyntaxTree.ParseText(File.ReadAllText(ownerPath), path: ownerPath).GetRoot();
		var decoderRoot = CSharpSyntaxTree.ParseText(File.ReadAllText(decoderPath), path: decoderPath).GetRoot();
		Assert.Empty(ownerRoot.DescendantNodes().OfType<StackAllocArrayCreationExpressionSyntax>());
		MethodDeclarationSyntax decoderEntryMethod = Assert.Single(
			decoderRoot.DescendantNodes().OfType<MethodDeclarationSyntax>(),
			method => method.Identifier.ValueText == "TryDecodeUntrustedStructuralResponse" && method.Body is not null);
		Assert.Equal(
			new[]
			{
				"ExpressionStatementSyntax|response=null;",
				"ExpressionStatementSyntax|errorCode=LiquidWalletFactsWireErrorCode.None;",
				"LocalDeclarationStatementSyntax|Span<byte>expectedSourceEpochScratch=stackallocbyte[SourceEpochLength];",
				"LocalDeclarationStatementSyntax|Span<byte>headerScratch=stackallocbyte[HeaderLength];",
				"LocalDeclarationStatementSyntax|byte[]?ownedFrame=null;",
				"LocalDeclarationStatementSyntax|int[]?transactionOffsets=null;",
				"LocalDeclarationStatementSyntax|boolownershipTransferred=false;",
			},
			GetPreTryStatementShapes(decoderEntryMethod));
		MethodDeclarationSyntax layoutValidatorMethod = Assert.Single(
			decoderRoot.DescendantNodes().OfType<MethodDeclarationSyntax>(),
			method => method.Identifier.ValueText == "TryValidateLayout");
		Assert.Equal(
			new[]
			{
				"LocalDeclarationStatementSyntax|varreader=newWireReader(frame,HeaderLength);",
				"LocalDeclarationStatementSyntax|Span<byte>previousTransactionId=stackallocbyte[32];",
				"LocalDeclarationStatementSyntax|boolhasPreviousTransactionId=false;",
				"LocalDeclarationStatementSyntax|inttotalInputCount=0;",
				"LocalDeclarationStatementSyntax|inttotalOwnedOutputCount=0;",
			},
			GetPreTryStatementShapes(layoutValidatorMethod));
		string[] actualStackallocs = decoderRoot.DescendantNodes()
			.OfType<StackAllocArrayCreationExpressionSyntax>()
			.Select(stackallocExpression =>
			{
				MethodDeclarationSyntax method = Assert.Single(stackallocExpression.Ancestors().OfType<MethodDeclarationSyntax>().Take(1));
				VariableDeclaratorSyntax variable = Assert.Single(stackallocExpression.Ancestors().OfType<VariableDeclaratorSyntax>().Take(1));
				ArrayTypeSyntax arrayType = Assert.IsType<ArrayTypeSyntax>(stackallocExpression.Type);
				ArrayRankSpecifierSyntax rank = Assert.Single(arrayType.RankSpecifiers);
				ExpressionSyntax size = Assert.Single(rank.Sizes);
				return $"{method.Identifier.ValueText}|{variable.Identifier.ValueText}|{arrayType.ElementType}|{size}";
			})
			.ToArray();
		Assert.Equal(
			new[]
			{
				"TryDecodeUntrustedStructuralResponse|expectedSourceEpochScratch|byte|SourceEpochLength",
				"TryDecodeUntrustedStructuralResponse|headerScratch|byte|HeaderLength",
				"TryValidateLayout|previousTransactionId|byte|32",
			},
			actualStackallocs);

		var stackScratchNames = new HashSet<string>(StringComparer.Ordinal)
		{
			"expectedSourceEpochScratch",
			"headerScratch",
			"previousTransactionId",
		};
		IdentifierNameSyntax[] scratchUses = decoderRoot.DescendantNodes()
			.OfType<IdentifierNameSyntax>()
			.Where(identifier => stackScratchNames.Contains(identifier.Identifier.ValueText))
			.ToArray();
		Assert.Equal(
			new[]
			{
				"expectedSourceEpochScratch|5",
				"headerScratch|6",
				"previousTransactionId|3",
			},
			scratchUses
				.GroupBy(identifier => identifier.Identifier.ValueText, StringComparer.Ordinal)
				.OrderBy(group => group.Key, StringComparer.Ordinal)
				.Select(group => $"{group.Key}|{group.Count()}"));
		Assert.Equal(
			"d2d0d87af83036f421c29fc31cd47822a7cd5b346bdf7bc959a3354e39d9180e",
			ComputeScratchUseFingerprint(scratchUses));
		string[] actualStackZeroization = decoderRoot.DescendantNodes()
			.OfType<InvocationExpressionSyntax>()
			.Where(invocation => string.Concat(invocation.Expression.DescendantTokens().Select(token => token.Text)) == "CryptographicOperations.ZeroMemory")
			.Select(invocation =>
			{
				ArgumentSyntax argument = Assert.Single(invocation.ArgumentList.Arguments);
				IdentifierNameSyntax? identifier = argument.Expression as IdentifierNameSyntax;
				return (Invocation: invocation, Identifier: identifier?.Identifier.ValueText);
			})
			.Where(item => item.Identifier is not null && stackScratchNames.Contains(item.Identifier))
			.Select(item =>
			{
				string identifier = Assert.IsType<string>(item.Identifier);
				MethodDeclarationSyntax method = Assert.Single(item.Invocation.Ancestors().OfType<MethodDeclarationSyntax>().Take(1));
				FinallyClauseSyntax finallyClause = Assert.Single(item.Invocation.Ancestors().OfType<FinallyClauseSyntax>().Take(1));
				TryStatementSyntax tryStatement = Assert.IsType<TryStatementSyntax>(finallyClause.Parent);
				ExpressionStatementSyntax zeroizationStatement = Assert.IsType<ExpressionStatementSyntax>(item.Invocation.Parent);
				VariableDeclaratorSyntax variable = Assert.Single(
					method.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
					candidate => candidate.Identifier.ValueText == identifier);
				LocalDeclarationStatementSyntax declaration = Assert.Single(variable.Ancestors().OfType<LocalDeclarationStatementSyntax>().Take(1));
				int declarationIndex = method.Body!.Statements.IndexOf(declaration);
				int tryIndex = method.Body.Statements.IndexOf(tryStatement);
				int directFinallyStatementIndex = finallyClause.Block.Statements.IndexOf(zeroizationStatement);
				bool aliasOrRefExposure = HasScratchAliasOrRefExposure(method, declaration, identifier);
				bool postZeroizationUse = HasScratchUseAfterZeroization(zeroizationStatement, identifier);
				bool exactFinallyCoverage = tryStatement.Finally == finallyClause &&
					declarationIndex >= 0 &&
					tryIndex > declarationIndex &&
					directFinallyStatementIndex >= 0 &&
					!aliasOrRefExposure &&
					!postZeroizationUse;
				return $"{method.Identifier.ValueText}|{identifier}|{directFinallyStatementIndex}|{aliasOrRefExposure}|{postZeroizationUse}|{exactFinallyCoverage}";
			})
			.ToArray();
		Assert.Equal(
			new[]
			{
				"TryDecodeUntrustedStructuralResponse|expectedSourceEpochScratch|0|False|False|True",
				"TryDecodeUntrustedStructuralResponse|headerScratch|1|False|False|True",
				"TryValidateLayout|previousTransactionId|0|False|False|True",
			},
			actualStackZeroization);

		MethodDeclarationSyntax headerValidator = Assert.Single(
			decoderRoot.DescendantNodes().OfType<MethodDeclarationSyntax>(),
			method => method.Identifier.ValueText == "TryValidateHeader");
		IfStatementSyntax magicCheck = Assert.Single(
			headerValidator.DescendantNodes().OfType<IfStatementSyntax>(),
			statement => string.Concat(statement.Condition.DescendantTokens().Select(token => token.Text)).StartsWith("header[0]", StringComparison.Ordinal));
		Assert.Equal(
			"header[0]!=(byte)'W'||header[1]!=(byte)'L'||header[2]!=(byte)'F'||header[3]!=(byte)'V'",
			string.Concat(magicCheck.Condition.DescendantTokens().Select(token => token.Text)));

		Assert.False(File.Exists(Path.Combine(productionRoot, "Liquid", "WalletFacts", "Wire", "LiquidWalletFactsWireV1Response.cs")));
		Assert.False(File.Exists(Path.Combine(productionRoot, "Liquid", "WalletFacts", "Wire", "LiquidWalletFactsWireV1ResponseCodec.cs")));
	}

	private static bool ContainsIdentifier(CSharpSyntaxNode node, string identifier) =>
		node.DescendantNodesAndSelf()
			.OfType<IdentifierNameSyntax>()
			.Any(candidate => candidate.Identifier.ValueText == identifier);

	private static bool ContainsPreprocessorDirective(CSharpSyntaxNode root) =>
		root.DescendantTrivia(descendIntoTrivia: true)
			.Any(trivia => trivia.GetStructure() is DirectiveTriviaSyntax);

	private static bool IsExactFriendAssemblyInventory(IEnumerable<string> consumers) =>
		consumers.SequenceEqual(["WalletWasabi.Tests"], StringComparer.Ordinal);

	private static bool HasScratchAliasOrRefExposure(
		MethodDeclarationSyntax method,
		LocalDeclarationStatementSyntax declaration,
		string identifier) =>
		method.Body!.DescendantNodes()
			.Where(node => node.SpanStart >= declaration.Span.End)
			.Any(node => node switch
			{
				VariableDeclaratorSyntax variable when variable.Initializer is not null => ContainsIdentifier(variable.Initializer.Value, identifier),
				RefExpressionSyntax reference => ContainsIdentifier(reference, identifier),
				AssignmentExpressionSyntax assignment =>
					ContainsIdentifier(assignment.Left, identifier) || ContainsIdentifier(assignment.Right, identifier),
				PrefixUnaryExpressionSyntax prefix when prefix.RawKind is (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression =>
					ContainsIdentifier(prefix.Operand, identifier),
				PostfixUnaryExpressionSyntax postfix when postfix.RawKind is (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression =>
					ContainsIdentifier(postfix.Operand, identifier),
				ArgumentSyntax argument when argument.RefKindKeyword.RawKind is (int)SyntaxKind.RefKeyword or (int)SyntaxKind.OutKeyword =>
					ContainsIdentifier(argument.Expression, identifier),
				_ => false,
			});

	private static bool HasScratchUseAfterZeroization(ExpressionStatementSyntax zeroizationStatement, string identifier)
	{
		MethodDeclarationSyntax method = Assert.Single(zeroizationStatement.Ancestors().OfType<MethodDeclarationSyntax>().Take(1));
		return method.Body!.DescendantNodes()
			.OfType<IdentifierNameSyntax>()
			.Any(candidate => candidate.SpanStart >= zeroizationStatement.Span.End && candidate.Identifier.ValueText == identifier);
	}

	private static string ComputeScratchUseFingerprint(IEnumerable<IdentifierNameSyntax> scratchUses)
	{
		string[] lines = scratchUses.Select(identifier =>
		{
			MethodDeclarationSyntax method = Assert.Single(identifier.Ancestors().OfType<MethodDeclarationSyntax>().Take(1));
			StatementSyntax statement = Assert.Single(identifier.Ancestors().OfType<StatementSyntax>().Take(1));
			string ancestry = string.Join(">", identifier.AncestorsAndSelf()
				.TakeWhile(node => node != method)
				.Reverse()
				.Select(node => node.RawKind));
			string normalizedStatement = string.Concat(statement.DescendantTokens().Select(token => token.Text));
			return $"{method.Identifier.ValueText}|{identifier.Identifier.ValueText}|{ancestry}|{normalizedStatement}";
		}).ToArray();
		byte[] digest = System.Security.Cryptography.SHA256.HashData(
			System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)));
		return Convert.ToHexString(digest).ToLowerInvariant();
	}

	private static string[] GetPreTryStatementShapes(MethodDeclarationSyntax method)
	{
		Assert.Single(method.Body!.Statements.OfType<TryStatementSyntax>());
		return method.Body.Statements
			.TakeWhile(statement => statement is not TryStatementSyntax)
			.Select(statement => $"{statement.GetType().Name}|{string.Concat(statement.DescendantTokens().Select(token => token.Text))}")
			.ToArray();
	}

	private static IEnumerable<(OpCode Opcode, MemberInfo Member)> ReferencedMembersWithOpcodes(MethodBase method)
	{
		foreach ((OpCode opcode, MemberInfo? member) in ReadInstructions(method))
		{
			if (member is not null)
			{
				yield return (opcode, member);
			}
		}
	}

	private static IEnumerable<OpCode> ReadOpCodes(MethodBase method) =>
		ReadInstructions(method).Select(instruction => instruction.Opcode);

	private static IEnumerable<(OpCode Opcode, MemberInfo? Member)> ReadInstructions(MethodBase method)
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
			MemberInfo? member = null;
			switch (opcode.OperandType)
			{
				case OperandType.InlineField:
				case OperandType.InlineMethod:
				case OperandType.InlineTok:
				case OperandType.InlineType:
					{
						int token = BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(offset, sizeof(int)));
						offset += sizeof(int);
						member = method.Module.ResolveMember(
							token,
							method.DeclaringType?.GetGenericArguments(),
							method.IsGenericMethod ? method.GetGenericArguments() : null);
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

			yield return (opcode, member);
		}
	}

	private static readonly IReadOnlyDictionary<short, OpCode> OpcodeByValue = typeof(OpCodes)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(field => field.FieldType == typeof(OpCode))
		.Select(field => (OpCode)field.GetValue(null)!)
		.ToDictionary(opcode => opcode.Value);

	private static void AssertResponseEquals(ExpectedResponse expected, LiquidWalletFactsWireV1UntrustedStructuralResponse actual)
	{
		Assert.Equal(expected.SourceEpoch, actual.GetSourceEpoch());
		Assert.Equal(expected.Transactions.Length, actual.TransactionCount);
		Assert.Equal(expected.Transactions.Sum(transaction => transaction.Outputs.Length), actual.OwnedOutputCount);
		Assert.Equal(expected.Transactions.Length == 0, actual.IsEmpty);
		for (int transactionIndex = 0; transactionIndex < expected.Transactions.Length; transactionIndex++)
		{
			ExpectedTransaction expectedTransaction = expected.Transactions[transactionIndex];
			LiquidWalletFactsWireV1UntrustedStructuralTransactionView actualTransaction = actual.GetTransaction(transactionIndex);
			Assert.Equal(expectedTransaction.TransactionId, actualTransaction.GetTransactionIdConsensusBytes());
			Assert.Equal(expectedTransaction.WitnessBinding, actualTransaction.GetTransactionWitnessBinding());
			Assert.Equal(expectedTransaction.Inputs.Length, actualTransaction.InputCount);
			Assert.Equal(expectedTransaction.Outputs.Length, actualTransaction.OwnedOutputCount);
			for (int inputIndex = 0; inputIndex < expectedTransaction.Inputs.Length; inputIndex++)
			{
				ExpectedInput expectedInput = expectedTransaction.Inputs[inputIndex];
				LiquidWalletFactsWireV1UntrustedStructuralInputView actualInput = actualTransaction.GetInput(inputIndex);
				Assert.Equal(expectedInput.PreviousTransactionId, actualInput.GetPreviousTransactionIdConsensusBytes());
				Assert.Equal(expectedInput.PreviousOutputIndex, actualInput.PreviousOutputIndex);
			}
			for (int outputIndex = 0; outputIndex < expectedTransaction.Outputs.Length; outputIndex++)
			{
				ExpectedOutput expectedOutput = expectedTransaction.Outputs[outputIndex];
				LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView actualOutput = actualTransaction.GetOwnedOutput(outputIndex);
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

	[AttributeUsage(AttributeTargets.Class)]
	private sealed class MetadataTypeCarrierAttribute : Attribute
	{
		public MetadataTypeCarrierAttribute(Type scalarType)
		{
			ScalarType = scalarType;
		}

		public Type ScalarType { get; }

		public Type[] Types { get; set; } = [];
	}

	[AttributeUsage(AttributeTargets.Class)]
	private sealed class MetadataGenericTypeCarrierAttribute<T> : Attribute
	{
	}

	[MetadataTypeCarrier(typeof(LiquidWalletFactsWireV1UntrustedStructuralResponse))]
	private sealed class TargetScalarAttributeFixture
	{
	}

	[MetadataTypeCarrier(typeof(object), Types = new Type[]
	{
		typeof(LiquidWalletFactsWireV1UntrustedStructuralTransactionView),
		typeof(LiquidWalletFactsWireV1UntrustedStructuralInputView),
		typeof(LiquidWalletFactsWireV1UntrustedStructuralOwnedOutputView),
	})]
	private sealed class TargetArrayAttributeFixture
	{
	}

	[MetadataGenericTypeCarrier<LiquidWalletFactsWireV1UntrustedStructuralResponse>]
	private sealed class TargetGenericAttributeFixture
	{
	}

	[MetadataTypeCarrier(typeof(WalletWasabi.Liquid.Wallet.LiquidWalletState))]
	private sealed class ForbiddenWalletScalarAttributeFixture
	{
	}

	[MetadataTypeCarrier(typeof(System.Runtime.Loader.AssemblyLoadContext))]
	private sealed class ForbiddenNativeLoaderScalarAttributeFixture
	{
	}

	[MetadataTypeCarrier(typeof(object), Types = new Type[] { typeof(WalletWasabi.Liquid.Wallet.LiquidWalletState) })]
	private sealed class ForbiddenWalletArrayAttributeFixture
	{
	}

	[MetadataTypeCarrier(typeof(object), Types = new Type[] { typeof(System.Runtime.Loader.AssemblyLoadContext) })]
	private sealed class ForbiddenNativeLoaderArrayAttributeFixture
	{
	}

	[MetadataGenericTypeCarrier<WalletWasabi.Liquid.Wallet.LiquidWalletState>]
	private sealed class ForbiddenWalletGenericAttributeFixture
	{
	}

	[MetadataGenericTypeCarrier<System.Runtime.Loader.AssemblyLoadContext>]
	private sealed class ForbiddenNativeLoaderGenericAttributeFixture
	{
	}

	[MetadataTypeCarrier(typeof(WalletWasabi.Stores.TransactionSqliteStorage))]
	private sealed class DisallowedDependencyScalarAttributeFixture
	{
	}

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
