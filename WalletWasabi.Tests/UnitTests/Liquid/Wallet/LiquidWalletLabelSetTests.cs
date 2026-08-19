using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Wallet;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.Wallet;

public class LiquidWalletLabelSetTests
{
	private const string ExpectedDenyBitmapSha256 =
		"aba8259fb7fddc67b76e3852b118f5fed9986e4b8e282b6a0ba18179a7f785fd";
	private const string ExpectedWhiteSpaceSourceManifestSha256 =
		"b40a71be5b2eab94e714994b3c4144f3a938ffd1c70aff8fb09a3560c1819bbe";
	private const string ExpectedFormatSourceManifestSha256 =
		"fb710516cdcadee8d1bd96c8d1c7aef616a019ac13902f0ffc6bae87b03ea895";
	private const string ExpectedDefaultIgnorableSourceManifestSha256 =
		"5f9862d434df57521085c47ead48286e3a730ced00f165d83e62a70bd97c6779";
	private const int ExpectedDeniedScalarCount = 4_355;
	private const int UnicodeDomainLength = 0x110000;
	private const int SurrogateStart = 0xd800;
	private const int SurrogateEnd = 0xdfff;
#if DEBUG
	private const string ExpectedImplementationManifestSha256 = "7a415e7e2a09e88bd22760acc26876c97df648d77babc16b35fcedf2b04589e2";
	private const string ExpectedProductionReferencesSha256 = "e0b8516c317a78c14b4ab083b1dcf9b1bc480e86b43f4e4f17ba769460f289bc";
	private const string ExpectedTestReferencesSha256 = "9a5309343f469180f80ce476bc252388b3c48ec873bdfaf43a798639ccc45a20";
#else
	private const string ExpectedImplementationManifestSha256 = "8efa59c7a9f708d59b601ae165e0e6eab2a58a2fd88a42164b03786fd6ae63b9";
	private const string ExpectedProductionReferencesSha256 = "e0b8516c317a78c14b4ab083b1dcf9b1bc480e86b43f4e4f17ba769460f289bc";
	private const string ExpectedTestReferencesSha256 = "742606719e1c60102876dc28cef3bcdc73ad1f341e608f398bfa37cfc38cef69";
#endif

	private static readonly ScalarRange[] ControlRanges =
	[
		new(0x0000, 0x001f),
		new(0x007f, 0x009f),
	];

	private static readonly ScalarRange[] NonAsciiWhiteSpaceRanges =
	[
		new(0x00a0, 0x00a0),
		new(0x1680, 0x1680),
		new(0x2000, 0x200a),
		new(0x2028, 0x2029),
		new(0x202f, 0x202f),
		new(0x205f, 0x205f),
		new(0x3000, 0x3000),
	];

	private static readonly ScalarRange[] WhiteSpaceSourceRanges =
	[
		new(0x0009, 0x000d),
		new(0x0020, 0x0020),
		new(0x0085, 0x0085),
		new(0x00a0, 0x00a0),
		new(0x1680, 0x1680),
		new(0x2000, 0x200a),
		new(0x2028, 0x2028),
		new(0x2029, 0x2029),
		new(0x202f, 0x202f),
		new(0x205f, 0x205f),
		new(0x3000, 0x3000),
	];

	private static readonly ScalarRange[] FormatRanges =
	[
		new(0x00ad, 0x00ad),
		new(0x0600, 0x0605),
		new(0x061c, 0x061c),
		new(0x06dd, 0x06dd),
		new(0x070f, 0x070f),
		new(0x0890, 0x0891),
		new(0x08e2, 0x08e2),
		new(0x180e, 0x180e),
		new(0x200b, 0x200f),
		new(0x202a, 0x202e),
		new(0x2060, 0x2064),
		new(0x2066, 0x206f),
		new(0xfeff, 0xfeff),
		new(0xfff9, 0xfffb),
		new(0x110bd, 0x110bd),
		new(0x110cd, 0x110cd),
		new(0x13430, 0x1343f),
		new(0x1bca0, 0x1bca3),
		new(0x1d173, 0x1d17a),
		new(0xe0001, 0xe0001),
		new(0xe0020, 0xe007f),
	];

	private static readonly ScalarRange[] DefaultIgnorableRanges =
	[
		new(0x00ad, 0x00ad),
		new(0x034f, 0x034f),
		new(0x061c, 0x061c),
		new(0x115f, 0x1160),
		new(0x17b4, 0x17b5),
		new(0x180b, 0x180f),
		new(0x200b, 0x200f),
		new(0x202a, 0x202e),
		new(0x2060, 0x206f),
		new(0x3164, 0x3164),
		new(0xfe00, 0xfe0f),
		new(0xfeff, 0xfeff),
		new(0xffa0, 0xffa0),
		new(0xfff0, 0xfff8),
		new(0x1bca0, 0x1bca3),
		new(0x1d173, 0x1d17a),
		new(0xe0000, 0xe0fff),
	];

	private static readonly ScalarRange[] DefaultIgnorableSourceRanges =
	[
		new(0x00ad, 0x00ad),
		new(0x034f, 0x034f),
		new(0x061c, 0x061c),
		new(0x115f, 0x1160),
		new(0x17b4, 0x17b5),
		new(0x180b, 0x180d),
		new(0x180e, 0x180e),
		new(0x180f, 0x180f),
		new(0x200b, 0x200f),
		new(0x202a, 0x202e),
		new(0x2060, 0x2064),
		new(0x2065, 0x2065),
		new(0x2066, 0x206f),
		new(0x3164, 0x3164),
		new(0xfe00, 0xfe0f),
		new(0xfeff, 0xfeff),
		new(0xffa0, 0xffa0),
		new(0xfff0, 0xfff8),
		new(0x1bca0, 0x1bca3),
		new(0x1d173, 0x1d17a),
		new(0xe0000, 0xe0000),
		new(0xe0001, 0xe0001),
		new(0xe0002, 0xe001f),
		new(0xe0020, 0xe007f),
		new(0xe0080, 0xe00ff),
		new(0xe0100, 0xe01ef),
		new(0xe01f0, 0xe0fff),
	];

	private static readonly IReadOnlyList<ScalarRange> AllLiteralRanges =
		ControlRanges
			.Concat(NonAsciiWhiteSpaceRanges)
			.Concat(FormatRanges)
			.Concat(DefaultIgnorableRanges)
			.Append(new ScalarRange(0xfdd0, 0xfdef))
			.ToArray();

	private static readonly ScalarRange[] FormatDefaultIgnorableOverlapRanges =
	[
		new(0x00ad, 0x00ad),
		new(0x061c, 0x061c),
		new(0x180e, 0x180e),
		new(0x200b, 0x200f),
		new(0x202a, 0x202e),
		new(0x2060, 0x2064),
		new(0x2066, 0x206f),
		new(0xfeff, 0xfeff),
		new(0x1bca0, 0x1bca3),
		new(0x1d173, 0x1d17a),
		new(0xe0001, 0xe0001),
		new(0xe0020, 0xe007f),
	];

	private static readonly string[] ApprovedReferenceKeys =
	[
		"F|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|<Empty>k__BackingField|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet",
		"F|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|_labels|System.String[]",
		"M|System.ArgumentException|.ctor||System.String,System.String|void",
		"M|System.ArgumentNullException|ThrowIfNull||System.Object,System.String|System.Void",
		"M|System.ArgumentOutOfRangeException|.ctor||System.String,System.String|void",
		"M|System.Array|AsReadOnly|System.String|System.String[]|System.Collections.ObjectModel.ReadOnlyCollection`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]",
		"M|System.Array|Copy||System.Array,System.Array,System.Int32|System.Void",
		"M|System.Array|Empty|System.String||System.String[]",
		"M|System.Array|Sort|System.String|System.String[],System.Collections.Generic.IComparer`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]|System.Void",
		"M|System.Collections.Generic.HashSet`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]|.ctor||System.Collections.Generic.IEqualityComparer`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]|void",
		"M|System.Collections.Generic.HashSet`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]|Add||System.String|System.Boolean",
		"M|System.Collections.Generic.HashSet`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]|CopyTo||System.String[]|System.Void",
		"M|System.Collections.Generic.HashSet`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]|get_Count|||System.Int32",
		"M|System.Collections.Generic.IReadOnlyCollection`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]|get_Count|||System.Int32",
		"M|System.Collections.Generic.IReadOnlyList`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]|get_Item||System.Int32|System.String",
		"M|System.HashCode|Add|System.String|System.String,System.Collections.Generic.IEqualityComparer`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]|System.Void",
		"M|System.HashCode|ToHashCode|||System.Int32",
		"M|System.IDisposable|Dispose|||System.Void",
		"M|System.Object|.ctor|||void",
		"M|System.StringComparer|Equals||System.String,System.String|System.Boolean",
		"M|System.StringComparer|get_Ordinal|||System.StringComparer",
		"M|System.String|EnumerateRunes|||System.Text.StringRuneEnumerator",
		"M|System.String|Substring||System.Int32,System.Int32|System.String",
		"M|System.String|get_Chars||System.Int32|System.Char",
		"M|System.String|get_Length|||System.Int32",
		"M|System.Text.Encoding|GetByteCount||System.String|System.Int32",
		"M|System.Text.Rune|get_Value|||System.Int32",
		"M|System.Text.StringRuneEnumerator|GetEnumerator|||System.Text.StringRuneEnumerator",
		"M|System.Text.StringRuneEnumerator|MoveNext|||System.Boolean",
		"M|System.Text.StringRuneEnumerator|get_Current|||System.Text.Rune",
		"M|System.Text.UTF8Encoding|.ctor||System.Boolean,System.Boolean|void",
		"M|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|.ctor||System.String[]|void",
		"M|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|Equals||WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|System.Boolean",
		"M|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|IsDeniedScalar||System.Int32|System.Boolean",
		"M|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|get_Count|||System.Int32",
		"M|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|get_Empty|||WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet",
		"M|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|op_Equality||WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet,WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet|System.Boolean",
		"T|System.HashCode",
		"T|System.Int32",
		"T|System.String",
		"T|System.Text.StringRuneEnumerator",
		"T|WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet",
	];

	[Fact]
	public void ExactSurfaceIsFrozen()
	{
		Type type = typeof(LiquidWalletLabelSet);
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

		Assert.True(type.IsClass);
		Assert.True(type.IsSealed);
		Assert.False(type.IsAbstract);
		Assert.False(type.IsPublic);
		Assert.False(type.IsNested);
		Assert.Equal(typeof(object), type.BaseType);
		Assert.Equal([typeof(IEquatable<LiquidWalletLabelSet>)], type.GetInterfaces());
		Assert.Empty(type.GetNestedTypes(Declared));
		Assert.Empty(type.GetEvents(Declared));

		ConstructorInfo constructor = Assert.Single(
			type.GetConstructors(Declared),
			candidate => !candidate.IsStatic);
		Assert.True(constructor.IsPrivate);
		Assert.Equal([typeof(string[])], constructor.GetParameters().Select(parameter => parameter.ParameterType));

		Assert.Equal(
			[
				"Count:System.Int32:instance",
				"Empty:WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet:static",
				"IsEmpty:System.Boolean:instance",
			],
			type.GetProperties(Declared)
				.Select(property =>
					$"{property.Name}:{property.PropertyType.FullName}:" +
					$"{(property.GetMethod!.IsStatic ? "static" : "instance")}")
				.OrderBy(value => value, StringComparer.Ordinal));
		Assert.All(type.GetProperties(Declared), property => Assert.Empty(property.GetIndexParameters()));

		FieldInfo[] publicFields = type.GetFields(Declared).Where(field => field.IsPublic).ToArray();
		Assert.Equal(
			[
				"MaximumLabelCount:System.Int32:32",
				"MaximumLabelUtf8ByteCount:System.Int32:128",
				"MaximumRawLabelUtf16CodeUnitCount:System.Int32:128",
				"MaximumTotalUtf8ByteCount:System.Int32:2048",
			],
			publicFields
				.Select(field => $"{field.Name}:{field.FieldType.FullName}:{field.GetRawConstantValue()}")
				.OrderBy(value => value, StringComparer.Ordinal));
		Assert.All(publicFields, field =>
		{
			Assert.True(field.IsLiteral);
			Assert.False(field.IsInitOnly);
		});
		Assert.DoesNotContain(type.GetFields(Declared), field => field.IsFamily || field.IsAssembly);
		Assert.Equal(
			[
				"<Empty>k__BackingField:WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet:static:readonly",
				"_labels:System.String[]:instance:readonly",
			],
			type.GetFields(Declared)
				.Where(field => field.IsPrivate && !field.IsLiteral)
				.Select(field =>
					$"{field.Name}:{field.FieldType.FullName}:" +
					$"{(field.IsStatic ? "static" : "instance")}:" +
					$"{(field.IsInitOnly ? "readonly" : "mutable")}")
				.OrderBy(value => value, StringComparer.Ordinal));

		string[] expectedPublicMethods =
		[
			"Create(System.Collections.Generic.IReadOnlyList`1[System.String])->WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet:static",
			"Equals(System.Object)->System.Boolean:instance",
			"Equals(WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet)->System.Boolean:instance",
			"GetHashCode()->System.Int32:instance",
			"GetLabels()->System.Collections.Generic.IReadOnlyList`1[System.String]:instance",
			"ToString()->System.String:instance",
			"get_Count()->System.Int32:instance",
			"get_Empty()->WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet:static",
			"get_IsEmpty()->System.Boolean:instance",
			"op_Equality(WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet,WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet)->System.Boolean:static",
			"op_Inequality(WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet,WalletWasabi.Liquid.Wallet.LiquidWalletLabelSet)->System.Boolean:static",
		];
		Assert.Equal(
			expectedPublicMethods.OrderBy(value => value, StringComparer.Ordinal),
			type.GetMethods(Declared)
				.Where(method => method.IsPublic)
				.Select(MethodSignature)
				.OrderBy(value => value, StringComparer.Ordinal));
		Assert.DoesNotContain(type.GetMethods(Declared), method => method.IsFamily || method.IsAssembly);
		Assert.Equal(
			["IsDeniedScalar(System.Int32)->System.Boolean:static"],
			type.GetMethods(Declared)
				.Where(method => method.IsPrivate)
				.Select(MethodSignature));

		Assert.Equal(LiquidWalletLabelSet.MaximumLabelCount, 32);
		Assert.Equal(LiquidWalletLabelSet.MaximumRawLabelUtf16CodeUnitCount, 128);
		Assert.Equal(LiquidWalletLabelSet.MaximumLabelUtf8ByteCount, 128);
		Assert.Equal(LiquidWalletLabelSet.MaximumTotalUtf8ByteCount, 2_048);
	}

	[Fact]
	public void ExactImplementationMetadataAndAssemblyReferencesAreFrozen()
	{
		string implementation = Sha256Utf8(BuildImplementationManifest(typeof(LiquidWalletLabelSet)));
		string productionReferences = Sha256Utf8(BuildAssemblyReferenceManifest(typeof(LiquidWalletLabelSet).Assembly));
		string testReferences = Sha256Utf8(BuildAssemblyReferenceManifest(typeof(LiquidWalletLabelSetTests).Assembly));
		Assert.True(
			StringComparer.Ordinal.Equals(ExpectedImplementationManifestSha256, implementation),
			implementation);
		Assert.Equal(ExpectedProductionReferencesSha256, productionReferences);
		Assert.True(
			StringComparer.Ordinal.Equals(ExpectedTestReferencesSha256, testReferences),
			testReferences);
	}

	[Fact]
	public void EmptyEqualityHashAndOperatorsUseOrdinaryValueSemantics()
	{
		LiquidWalletLabelSet empty = LiquidWalletLabelSet.Empty;
		LiquidWalletLabelSet created = LiquidWalletLabelSet.Create([]);
		ConstructorInfo constructor = Assert.Single(typeof(LiquidWalletLabelSet).GetConstructors(
			BindingFlags.NonPublic | BindingFlags.Instance));
		var independent = Assert.IsType<LiquidWalletLabelSet>(constructor.Invoke([Array.Empty<string>()]));

		Assert.Same(empty, created);
		Assert.NotSame(empty, independent);
		Assert.True(empty.IsEmpty);
		Assert.Equal(0, empty.Count);
		Assert.Empty(empty.GetLabels());
		Assert.Equal(empty, independent);
		Assert.True(empty == independent);
		Assert.False(empty != independent);
		Assert.Equal(empty.GetHashCode(), independent.GetHashCode());
		Assert.True(((object)empty).Equals(independent));
		Assert.False(empty.Equals(null));
		Assert.False(empty.Equals(new object()));
		Assert.Equal(nameof(LiquidWalletLabelSet), empty.ToString());

		LiquidWalletLabelSet? left = null;
		LiquidWalletLabelSet? right = null;
		Assert.True(left == right);
		Assert.False(left != right);
		Assert.False(left == empty);
		Assert.True(left != empty);
		Assert.False(empty == right);
		Assert.True(empty != right);

		LiquidWalletLabelSet one = LiquidWalletLabelSet.Create(["one"]);
		LiquidWalletLabelSet other = LiquidWalletLabelSet.Create(["other"]);
		LiquidWalletLabelSet two = LiquidWalletLabelSet.Create(["one", "two"]);
		Assert.NotEqual(one, other);
		Assert.False(one == other);
		Assert.True(one != other);
		Assert.NotEqual(one, two);
		Assert.False(one == two);
		Assert.True(one != two);
	}

	[Fact]
	public void CanonicalizationIsOrdinalDeterministicAndDelimiterSafe()
	{
		string composed = "caf\u00e9";
		string decomposed = "cafe\u0301";
		LiquidWalletLabelSet first = LiquidWalletLabelSet.Create(
			[" beta ", "Alpha", "alpha", "comma,label", "colon:label", composed, decomposed, "beta"]);
		LiquidWalletLabelSet second = LiquidWalletLabelSet.Create(
			[decomposed, "colon:label", "beta", composed, "comma,label", "alpha", "Alpha"]);
		string[] expected =
		[
			"Alpha",
			"alpha",
			"beta",
			decomposed,
			composed,
			"colon:label",
			"comma,label",
		];
		Array.Sort(expected, StringComparer.Ordinal);

		Assert.Equal(expected, first.GetLabels());
		Assert.Equal(expected, second.GetLabels());
		Assert.Equal(first, second);
		Assert.True(first == second);
		Assert.Equal(first.GetHashCode(), second.GetHashCode());
		Assert.Equal(7, first.Count);
		Assert.False(first.IsEmpty);
		Assert.Equal(nameof(LiquidWalletLabelSet), first.ToString());
		Assert.Contains(composed, first.GetLabels());
		Assert.Contains(decomposed, first.GetLabels());
		Assert.NotEqual(composed, decomposed);
	}

	[Fact]
	public void CanonicalizationIsOrdinalOnTheInvariantGlobalizationTarget()
	{
		Assert.Equal(
			"true",
			Assert.IsType<string>(AppContext.GetData("System.Globalization.Invariant")));
		Assert.Same(CultureInfo.InvariantCulture, CultureInfo.CurrentCulture);
		Assert.Same(CultureInfo.InvariantCulture, CultureInfo.CurrentUICulture);
		string[] labels = ["I", "i", "\u0130", "\u0131", "caf\u00e9", "cafe\u0301"];
		LiquidWalletLabelSet first = LiquidWalletLabelSet.Create(labels);
		LiquidWalletLabelSet reversed = LiquidWalletLabelSet.Create(labels.Reverse().ToArray());

		Assert.Equal(first, reversed);
		Assert.Equal(first.GetHashCode(), reversed.GetHashCode());
		Assert.Equal(labels.OrderBy(value => value, StringComparer.Ordinal), reversed.GetLabels());
	}

	[Fact]
	public void InputsAndReturnedViewsAreDefensivelyOwned()
	{
		string[] input = ["second", "first"];
		LiquidWalletLabelSet labels = LiquidWalletLabelSet.Create(input);
		input[0] = "mutated-input";
		input[1] = "mutated-input-again";

		IReadOnlyList<string> first = labels.GetLabels();
		IReadOnlyList<string> second = labels.GetLabels();
		Assert.Equal(["first", "second"], first);
		Assert.Equal(["first", "second"], second);
		Assert.NotSame(first, second);

		var mutableFirst = Assert.IsAssignableFrom<IList<string>>(first);
		Assert.True(mutableFirst.IsReadOnly);
		Assert.Throws<NotSupportedException>(() => mutableFirst[0] = "mutated-view");
		Assert.Throws<NotSupportedException>(() => mutableFirst.Add("added-view"));
		Assert.Throws<NotSupportedException>(() => mutableFirst.RemoveAt(0));
		Assert.Equal(["first", "second"], labels.GetLabels());
	}

	[Fact]
	public void CollectionAccessIsExactlyBoundedAndNeverEnumerates()
	{
		var probe = new AccessProbeReadOnlyList(["third", "first", "second"]);
		LiquidWalletLabelSet labels = LiquidWalletLabelSet.Create(probe);

		Assert.Equal(1, probe.CountReads);
		Assert.Equal([1, 1, 1], probe.IndexReads);
		Assert.Equal(0, probe.EnumeratorReads);
		Assert.Equal(["first", "second", "third"], labels.GetLabels());

		var negative = new AccessProbeReadOnlyList([], reportedCount: -1);
		AssertOwnedFailure<ArgumentOutOfRangeException>(() => LiquidWalletLabelSet.Create(negative));
		Assert.Equal(1, negative.CountReads);
		Assert.Empty(negative.IndexReads);
		Assert.Equal(0, negative.EnumeratorReads);

		var overCount = new AccessProbeReadOnlyList([], reportedCount: 33);
		AssertOwnedFailure<ArgumentOutOfRangeException>(() => LiquidWalletLabelSet.Create(overCount));
		Assert.Equal(1, overCount.CountReads);
		Assert.All(overCount.IndexReads, reads => Assert.Equal(0, reads));
		Assert.Equal(0, overCount.EnumeratorReads);
	}

	[Fact]
	public void CallerOwnedCountAndIndexerFailuresPropagateUnchanged()
	{
		var countFailure = new CallerOwnedCollectionException("caller-count-privacy-canary");
		var countThrows = new ThrowingReadOnlyList(countFailure, throwFromCount: true);
		Assert.Same(countFailure, Assert.Throws<CallerOwnedCollectionException>(() =>
			LiquidWalletLabelSet.Create(countThrows)));
		Assert.Equal(1, countThrows.CountReads);
		Assert.Equal(0, countThrows.IndexReads);
		Assert.Equal(0, countThrows.EnumeratorReads);

		var indexFailure = new CallerOwnedCollectionException("caller-index-privacy-canary");
		var indexThrows = new ThrowingReadOnlyList(indexFailure, throwFromCount: false);
		Assert.Same(indexFailure, Assert.Throws<CallerOwnedCollectionException>(() =>
			LiquidWalletLabelSet.Create(indexThrows)));
		Assert.Equal(1, indexThrows.CountReads);
		Assert.Equal(1, indexThrows.IndexReads);
		Assert.Equal(0, indexThrows.EnumeratorReads);
	}

	[Fact]
	public void RawUtf16AndUtf8BoundsAreExact()
	{
		string raw128 = "x" + new string(' ', 127);
		LiquidWalletLabelSet rawBoundary = LiquidWalletLabelSet.Create([raw128]);
		Assert.Equal("x", Assert.Single(rawBoundary.GetLabels()));

		string paddedOverCap = new string(' ', 128) + "x";
		AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
			LiquidWalletLabelSet.Create([paddedOverCap]), paddedOverCap);
		string veryLarge = new('z', 1_000_000);
		AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
			LiquidWalletLabelSet.Create([veryLarge]));

		string oneByte128 = new('a', 128);
		string twoByte128 = string.Concat(Enumerable.Repeat("\u00e9", 64));
		string threeByte126 = string.Concat(Enumerable.Repeat("\u20ac", 42));
		string fourByte128 = string.Concat(Enumerable.Repeat("\U0001f642", 32));
		foreach (string value in new[] { oneByte128, twoByte128, threeByte126, fourByte128 })
		{
			Assert.InRange(Encoding.UTF8.GetByteCount(value), 1, 128);
			Assert.Equal(value, Assert.Single(LiquidWalletLabelSet.Create([value]).GetLabels()));
		}

		string byte129 = fourByte128 + "a";
		Assert.Equal(129, Encoding.UTF8.GetByteCount(byte129));
		Assert.True(byte129.Length <= LiquidWalletLabelSet.MaximumRawLabelUtf16CodeUnitCount);
		AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
			LiquidWalletLabelSet.Create([byte129]), byte129);
		string fourByte132 = fourByte128 + "\U0001f642";
		Assert.Equal(132, Encoding.UTF8.GetByteCount(fourByte132));
		AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
			LiquidWalletLabelSet.Create([fourByte132]), fourByte132);
	}

	[Fact]
	public void CountAndUniqueUtf8TotalBoundsAreExact()
	{
		string[] thirtyTwo = Enumerable.Range(0, 32)
			.Select(index => $"label-{index:D2}")
			.ToArray();
		Assert.Equal(32, LiquidWalletLabelSet.Create(thirtyTwo).Count);

		var countThirtyThree = new AccessProbeReadOnlyList(
			Enumerable.Range(0, 33).Select(index => $"label-{index:D2}").ToArray());
		AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
			LiquidWalletLabelSet.Create(countThirtyThree));
		Assert.Equal(1, countThirtyThree.CountReads);
		Assert.All(countThirtyThree.IndexReads, reads => Assert.Equal(0, reads));

		string[] exactTotal = Enumerable.Range(0, 16)
			.Select(index =>
				index.ToString("x2", CultureInfo.InvariantCulture) +
				"\u00e9\u20ac\U0001f642" +
				new string('a', 117))
			.ToArray();
		Assert.Equal(2_048, exactTotal.Sum(Encoding.UTF8.GetByteCount));
		Assert.Equal(16, LiquidWalletLabelSet.Create(exactTotal).Count);

		string[] overTotal = [.. exactTotal, "b"];
		Assert.Equal(2_049, overTotal.Sum(Encoding.UTF8.GetByteCount));
		AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
			LiquidWalletLabelSet.Create(overTotal), overTotal[0]);

		string duplicate = new('d', 128);
		string[] duplicateHeavy = Enumerable.Repeat(duplicate, 32).ToArray();
		Assert.Equal(4_096, duplicateHeavy.Sum(Encoding.UTF8.GetByteCount));
		LiquidWalletLabelSet deduplicated = LiquidWalletLabelSet.Create(duplicateHeavy);
		Assert.Equal(1, deduplicated.Count);
		Assert.Equal(duplicate, Assert.Single(deduplicated.GetLabels()));
	}

	[Fact]
	public void InvalidUtf16RejectsAtEveryPositionWithoutReplacement()
	{
		foreach (char surrogate in new[] { '\ud800', '\udfff' })
		{
			foreach (string invalid in new[]
			{
				$"{surrogate}valid",
				$"valid{surrogate}value",
				$"valid{surrogate}",
			})
			{
				AssertOwnedFailure<ArgumentException>(() =>
					LiquidWalletLabelSet.Create([invalid]), invalid);
			}
		}
	}

	[Fact]
	public void SourceDerivedRangeManifestsAreFrozenAndNormalizeToV1Ranges()
	{
		AssertOrderedRangeManifest(WhiteSpaceSourceRanges);
		AssertOrderedRangeManifest(FormatRanges);
		AssertOrderedRangeManifest(DefaultIgnorableSourceRanges);
		Assert.Equal(ExpectedWhiteSpaceSourceManifestSha256, RangeManifestSha256(WhiteSpaceSourceRanges));
		Assert.Equal(ExpectedFormatSourceManifestSha256, RangeManifestSha256(FormatRanges));
		Assert.Equal(
			ExpectedDefaultIgnorableSourceManifestSha256,
			RangeManifestSha256(DefaultIgnorableSourceRanges));

		var whiteSpaceSupplement = new List<ScalarRange>();
		foreach (ScalarRange range in WhiteSpaceSourceRanges)
		{
			if (range.Start == 0x20 && range.End == 0x20)
			{
				continue;
			}
			if (IsRangeCoveredBy(range, ControlRanges))
			{
				continue;
			}
			whiteSpaceSupplement.Add(range);
		}

		Assert.Equal(NonAsciiWhiteSpaceRanges, MergeAdjacentRanges(whiteSpaceSupplement));
		Assert.Equal(DefaultIgnorableRanges, MergeAdjacentRanges(DefaultIgnorableSourceRanges));
	}

	[Fact]
	public void LiteralRangeEndpointsInteriorsAndNonOverlappingAdjacenciesAreBound()
	{
		AssertOrderedRangeManifest(NonAsciiWhiteSpaceRanges);
		AssertOrderedRangeManifest(FormatRanges);
		AssertOrderedRangeManifest(DefaultIgnorableRanges);
		foreach (ScalarRange range in AllLiteralRanges)
		{
			foreach (int scalar in new[] { range.Start, range.Start + ((range.End - range.Start) / 2), range.End }.Distinct())
			{
				Assert.True(IsDeniedByIndependentOracle(scalar), $"U+{scalar:X} must be denied by the oracle.");
				AssertDeniedAtEveryPosition(scalar);
			}

			foreach (int adjacent in new[] { range.Start - 1, range.End + 1 })
			{
				if (Rune.IsValid(adjacent) && adjacent != 0x20 && !IsDeniedByIndependentOracle(adjacent))
				{
					string accepted = RuneString(adjacent);
					Assert.Equal(accepted, Assert.Single(
						LiquidWalletLabelSet.Create([accepted]).GetLabels()));
				}
			}
		}

		string supplementaryVariationSelector = RuneString(0xe0100);
		foreach (string candidate in new[]
		{
			supplementaryVariationSelector,
			supplementaryVariationSelector + "visible",
			"visible" + supplementaryVariationSelector,
			"vis" + supplementaryVariationSelector + "ible",
		})
		{
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create([candidate]));
		}
	}

	[Fact]
	public void EveryIndependentDenyClassOverlapIsExplicitlyClassified()
	{
		var expectedOverlap = new Dictionary<int, DenyClass>();
		foreach (ScalarRange range in FormatDefaultIgnorableOverlapRanges)
		{
			for (int scalar = range.Start; scalar <= range.End; scalar++)
			{
				expectedOverlap.Add(scalar, DenyClass.Format | DenyClass.DefaultIgnorable);
			}
		}
		var actualOverlap = new Dictionary<int, DenyClass>();
		for (int scalar = 0; scalar < UnicodeDomainLength; scalar++)
		{
			if (!Rune.IsValid(scalar))
			{
				continue;
			}
			DenyClass classes = ClassifyByIndependentOracle(scalar);
			if (HasMoreThanOneBit(classes))
			{
				actualOverlap.Add(scalar, classes);
			}
		}

		Assert.Equal(expectedOverlap.OrderBy(pair => pair.Key), actualOverlap.OrderBy(pair => pair.Key));
	}

	[Fact]
	public void NoncharactersRejectAcrossPlanesAndPositions()
	{
		foreach (int scalar in new[]
		{
			0xfdd0, 0xfdef, 0xfffe, 0xffff, 0x1fffe, 0x1ffff, 0x7fffe, 0x7ffff, 0x10fffe, 0x10ffff,
		})
		{
			AssertDeniedAtEveryPosition(scalar);
		}
	}

	[Fact]
	public void ExhaustiveScalarPredicateMatchesIndependentFrozenOracle()
	{
		MethodInfo predicate = Assert.Single(typeof(LiquidWalletLabelSet).GetMethods(
			BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly), method =>
			method.ReturnType == typeof(bool) &&
			method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(int)]));
		Assert.Equal("IsDeniedScalar", predicate.Name);
		var denyScalar = predicate.CreateDelegate<Func<int, bool>>();
		var bitmap = new byte[UnicodeDomainLength / 8];
		int deniedCount = 0;

		for (int scalar = 0; scalar < UnicodeDomainLength; scalar++)
		{
			bool isSurrogate = scalar is >= SurrogateStart and <= SurrogateEnd;
			bool expected = !isSurrogate && IsDeniedByIndependentOracle(scalar);
			if (expected)
			{
				bitmap[scalar / 8] |= (byte)(1 << (7 - (scalar % 8)));
				deniedCount++;
			}

			if (!isSurrogate)
			{
				bool actual = denyScalar(scalar);
				Assert.True(actual == expected,
					$"The production scalar predicate disagrees with the independent V1 oracle at U+{scalar:X6}.");
			}
		}

		Assert.Equal(139_264, bitmap.Length);
		Assert.Equal(ExpectedDeniedScalarCount, deniedCount);
		Assert.Equal(
			ExpectedDenyBitmapSha256,
			Convert.ToHexString(SHA256.HashData(bitmap)).ToLowerInvariant());
		for (int surrogate = SurrogateStart; surrogate <= SurrogateEnd; surrogate++)
		{
			Assert.Equal(0, bitmap[surrogate / 8] & (1 << (7 - (surrogate % 8))));
		}
	}

	[Fact]
	public void EveryIndependentlyDeniedScalarRejectsThroughCreate()
	{
		int rejectedCount = 0;
		for (int scalar = 0; scalar < UnicodeDomainLength; scalar++)
		{
			if (!IsDeniedByIndependentOracle(scalar))
			{
				continue;
			}

			Assert.Throws<ArgumentException>(() =>
				LiquidWalletLabelSet.Create([RuneString(scalar)]));
			rejectedCount++;
		}
		Assert.Equal(ExpectedDeniedScalarCount, rejectedCount);
	}

	[Fact]
	public void PhasePrecedenceIsFailClosed()
	{
		string rawOverCap = new('r', 129);
		string invalidUtf16 = "invalid\ud800";
		string deniedScalar = "denied\u2066";
		string byteOverCap = string.Concat(Enumerable.Repeat("\U0001f642", 32)) + "a";

		AssertOwnedFailure<ArgumentException>(() =>
			LiquidWalletLabelSet.Create([rawOverCap, null!]));
		AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
			LiquidWalletLabelSet.Create([invalidUtf16, rawOverCap]));
		AssertOwnedFailure<ArgumentException>(() =>
			LiquidWalletLabelSet.Create(["   ", deniedScalar]));
		AssertOwnedFailure<ArgumentException>(() =>
			LiquidWalletLabelSet.Create([byteOverCap, "   "]));
	}

	[Fact]
	public void OwnedDiagnosticsAreFixedAndPrivacyRedacted()
	{
		const string Canary = "privacy-canary-92837465";
		string invalidUtf16 = Canary + "\ud800";
		string denied = Canary + "\u2066";
		string rawOverCap = Canary + new string('r', 129);
		string byteOverCap = string.Concat(Enumerable.Repeat("\U0001f642", 32)) + Canary[0];
		string[] totalOverCap =
		[
			.. Enumerable.Range(0, 16)
				.Select(index => index.ToString("x2", CultureInfo.InvariantCulture) + new string('a', 126)),
			Canary[0].ToString(),
		];
		AssertOwnedFailure<ArgumentNullException>(() => LiquidWalletLabelSet.Create(null!));

		Exception[] genericFailures =
		[
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create([null!])),
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create(["safe", null!])),
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create([""])),
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create(["safe", ""])),
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create([invalidUtf16]), invalidUtf16),
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create(["safe", invalidUtf16]), invalidUtf16),
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create([denied]), denied),
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create(["safe", denied]), denied),
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create(["   "])),
			AssertOwnedFailure<ArgumentException>(() => LiquidWalletLabelSet.Create(["safe", "   "])),
		];
		Assert.Single(genericFailures.Select(failure => failure.Message).Distinct(StringComparer.Ordinal));

		Exception[] rangeFailures =
		[
			AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
				LiquidWalletLabelSet.Create(new AccessProbeReadOnlyList([], reportedCount: 92_837_465))),
			AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
				LiquidWalletLabelSet.Create([rawOverCap]), rawOverCap),
			AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
				LiquidWalletLabelSet.Create(["safe", rawOverCap]), rawOverCap),
			AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
				LiquidWalletLabelSet.Create([byteOverCap])),
			AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
				LiquidWalletLabelSet.Create(["safe", byteOverCap])),
			AssertOwnedFailure<ArgumentOutOfRangeException>(() =>
				LiquidWalletLabelSet.Create(totalOverCap), totalOverCap[0]),
		];
		Assert.Single(rangeFailures.Select(failure => failure.Message).Distinct(StringComparer.Ordinal));

		foreach (Exception failure in genericFailures.Concat(rangeFailures))
		{
			Assert.DoesNotContain(Canary, failure.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(Canary, failure.ToString(), StringComparison.Ordinal);
			Assert.DoesNotContain("92837465", failure.Message, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ImplementationGraphUsesOnlyApprovedBoundedManagedSurfaces()
	{
		Type type = typeof(LiquidWalletLabelSet);
		MethodInfo create = Assert.Single(type.GetMethods(
			BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
			method => method.Name == nameof(LiquidWalletLabelSet.Create));
		MethodInfo denyPredicate = Assert.Single(type.GetMethods(
			BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly),
			method => method.Name == "IsDeniedScalar");
		MethodBase[] methods = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
			.Cast<MethodBase>()
			.Concat(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
				BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
			.ToArray();
		MemberInfo[] references = methods.SelectMany(GetIlReferences).Distinct().ToArray();
		MemberInfo[] createReferences = GetIlReferences(create).ToArray();
		Type[] approvedReferenceOwners =
		[
			type,
			typeof(ArgumentException),
			typeof(ArgumentNullException),
			typeof(ArgumentOutOfRangeException),
			typeof(Array),
			typeof(HashSet<string>),
			typeof(IReadOnlyCollection<string>),
			typeof(IReadOnlyList<string>),
			typeof(HashCode),
			typeof(IDisposable),
			typeof(int),
			typeof(object),
			typeof(string),
			typeof(StringComparer),
			typeof(Encoding),
			typeof(Rune),
			typeof(StringRuneEnumerator),
			typeof(UTF8Encoding),
		];
		Assert.Equal(
			ApprovedReferenceKeys,
			references.Select(ApprovedReferenceKey).OrderBy(value => value, StringComparer.Ordinal));

		Assert.Contains(references, member => member.Name == "EnumerateRunes");
		Assert.Contains(createReferences, member => member == denyPredicate);
		Assert.Contains(createReferences, member =>
			member.DeclaringType == typeof(string) && member.Name == "get_Chars");
		Assert.Contains(createReferences, member =>
			member.DeclaringType == typeof(string) && member.Name == "Substring");
		Assert.Contains(references, member =>
			member.DeclaringType == typeof(IReadOnlyCollection<string>) && member.Name == "get_Count");
		Assert.Contains(references, member =>
			member.DeclaringType == typeof(IReadOnlyList<string>) && member.Name == "get_Item");
		Assert.Contains(references, member =>
			member.DeclaringType == typeof(StringComparer) && member.Name == "get_Ordinal");
		Assert.Contains(references, member => member.DeclaringType == typeof(UTF8Encoding));

		foreach (MemberInfo reference in references)
		{
			Type? owner = reference as Type ?? reference.DeclaringType;
			Assert.Contains(owner, approvedReferenceOwners);
			Assert.False(IsForbiddenMember(reference), $"Forbidden member reference: {MemberIdentity(reference)}");
		}
		foreach (MethodBase method in methods)
		{
			Assert.Empty(GetIlSignatures(method));
			Assert.DoesNotContain(GetIlOpCodes(method),
				opCode => opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn ||
					opCode == OpCodes.Localloc);
			Assert.False(IsForbiddenType(method.DeclaringType), $"Forbidden owner: {method}");
			if (method is MethodInfo methodInfo)
			{
				Assert.False(IsForbiddenType(methodInfo.ReturnType), $"Forbidden return type: {method}");
				Assert.False(ContainsForbiddenModifiers(methodInfo.ReturnParameter),
					$"Forbidden return modifier: {method}");
			}
			Assert.All(method.GetParameters(), parameter =>
			{
				Assert.False(IsForbiddenType(parameter.ParameterType), $"Forbidden parameter: {method}");
				Assert.False(ContainsForbiddenModifiers(parameter), $"Forbidden parameter modifier: {method}");
			});
			Assert.All(method.GetMethodBody()?.LocalVariables ?? [], local =>
				Assert.False(IsForbiddenType(local.LocalType), $"Forbidden local: {method}"));
			Assert.All(method.GetMethodBody()?.ExceptionHandlingClauses ?? [], clause =>
				Assert.False(IsForbiddenType(clause.Flags == ExceptionHandlingClauseOptions.Clause
					? clause.CatchType
					: null), $"Forbidden catch type: {method}"));
		}
		FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
		PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
		Assert.All(fields, field =>
		{
			Assert.False(IsForbiddenType(field.FieldType), $"Forbidden field type: {field}");
			Assert.False(field.GetRequiredCustomModifiers().Any(IsForbiddenType),
				$"Forbidden required field modifier: {field}");
			Assert.False(field.GetOptionalCustomModifiers().Any(IsForbiddenType),
				$"Forbidden optional field modifier: {field}");
		});

		Assert.DoesNotContain(type.CustomAttributes, IsForbiddenAttribute);
		foreach (FieldInfo field in fields)
		{
			Assert.DoesNotContain(field.CustomAttributes, attribute =>
				IsForbiddenAttribute(attribute) &&
				!IsRequiredEmptyBackingFieldAttribute(field, attribute));
		}
		Assert.DoesNotContain(properties.SelectMany(property => property.CustomAttributes),
			IsForbiddenAttribute);
		Assert.DoesNotContain(methods.SelectMany(method => method.CustomAttributes),
			IsForbiddenAttribute);
		Assert.DoesNotContain(methods.OfType<MethodInfo>()
			.SelectMany(method => method.ReturnParameter.CustomAttributes),
			IsForbiddenAttribute);
		Assert.DoesNotContain(methods.SelectMany(method => method.GetParameters())
			.SelectMany(parameter => parameter.CustomAttributes),
			IsForbiddenAttribute);
	}

	private static T AssertOwnedFailure<T>(Action action, params string[] sensitiveValues)
		where T : ArgumentException
	{
		T failure = Assert.Throws<T>(action);
		Assert.Equal("labels", failure.ParamName);
		Assert.Null(failure.InnerException);
		Assert.Empty(failure.Data);
		if (failure is ArgumentOutOfRangeException outOfRange)
		{
			Assert.Null(outOfRange.ActualValue);
		}

		string rendered = failure.ToString();
		foreach (string sensitive in sensitiveValues.Where(value => !string.IsNullOrEmpty(value)))
		{
			Assert.DoesNotContain(sensitive, failure.Message, StringComparison.Ordinal);
			Assert.DoesNotContain(sensitive, rendered, StringComparison.Ordinal);
			if (!ContainsInvalidUtf16(sensitive))
			{
				byte[] encoded = Encoding.UTF8.GetBytes(sensitive);
				string hex = Convert.ToHexString(encoded);
				string base64 = Convert.ToBase64String(encoded);
				if (hex.Length >= 8)
				{
					Assert.DoesNotContain(hex, rendered, StringComparison.OrdinalIgnoreCase);
				}
				if (base64.Length >= 8)
				{
					Assert.DoesNotContain(base64, rendered, StringComparison.Ordinal);
				}
			}
		}
		return failure;
	}

	private static void AssertOwnedFailure<T>(Action action, IEnumerable<string> sensitiveValues)
		where T : ArgumentException =>
		AssertOwnedFailure<T>(action, sensitiveValues.ToArray());

	private static bool ContainsInvalidUtf16(string value)
	{
		try
		{
			_ = new UTF8Encoding(false, true).GetByteCount(value);
			return false;
		}
		catch (EncoderFallbackException)
		{
			return true;
		}
	}

	private static void AssertDeniedAtEveryPosition(int scalar)
	{
		string denied = RuneString(scalar);
		foreach (string candidate in new[] { denied, denied + "visible", "visible" + denied, "vis" + denied + "ible" })
		{
			AssertOwnedFailure<ArgumentException>(() =>
				LiquidWalletLabelSet.Create([candidate]), candidate);
		}
	}

	private static bool IsDeniedByIndependentOracle(int scalar)
	{
		if (!Rune.IsValid(scalar))
		{
			return false;
		}
		if (AllLiteralRanges.Any(range => range.Contains(scalar)))
		{
			return true;
		}
		return (scalar & 0xffff) is 0xfffe or 0xffff;
	}

	private static DenyClass ClassifyByIndependentOracle(int scalar)
	{
		DenyClass result = DenyClass.None;
		if (ControlRanges.Any(range => range.Contains(scalar)))
		{
			result |= DenyClass.Control;
		}
		if (NonAsciiWhiteSpaceRanges.Any(range => range.Contains(scalar)))
		{
			result |= DenyClass.WhiteSpace;
		}
		if (FormatRanges.Any(range => range.Contains(scalar)))
		{
			result |= DenyClass.Format;
		}
		if (DefaultIgnorableRanges.Any(range => range.Contains(scalar)))
		{
			result |= DenyClass.DefaultIgnorable;
		}
		if (scalar is >= 0xfdd0 and <= 0xfdef || (scalar & 0xffff) is 0xfffe or 0xffff)
		{
			result |= DenyClass.Noncharacter;
		}
		return result;
	}

	private static bool HasMoreThanOneBit(DenyClass value)
	{
		int bits = (int)value;
		return bits != 0 && (bits & (bits - 1)) != 0;
	}

	private static bool IsRangeCoveredBy(
		ScalarRange candidate,
		IReadOnlyList<ScalarRange> ranges)
	{
		foreach (ScalarRange range in ranges)
		{
			if (candidate.Start >= range.Start && candidate.End <= range.End)
			{
				return true;
			}
		}
		return false;
	}

	private static ScalarRange[] MergeAdjacentRanges(IReadOnlyList<ScalarRange> ranges)
	{
		var merged = new List<ScalarRange>();
		foreach (ScalarRange range in ranges)
		{
			if (merged.Count == 0 || range.Start > merged[^1].End + 1)
			{
				merged.Add(range);
				continue;
			}

			ScalarRange previous = merged[^1];
			merged[^1] = new ScalarRange(previous.Start, Math.Max(previous.End, range.End));
		}
		return merged.ToArray();
	}

	private static string RangeManifestSha256(IReadOnlyList<ScalarRange> ranges)
	{
		var manifest = new StringBuilder();
		foreach (ScalarRange range in ranges)
		{
			manifest.Append(range.Start.ToString("X6", CultureInfo.InvariantCulture));
			manifest.Append('-');
			manifest.Append(range.End.ToString("X6", CultureInfo.InvariantCulture));
			manifest.Append('\n');
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(manifest.ToString())))
			.ToLowerInvariant();
	}

	private static void AssertOrderedRangeManifest(IReadOnlyList<ScalarRange> ranges)
	{
		for (int index = 0; index < ranges.Count; index++)
		{
			Assert.InRange(ranges[index].Start, 0, 0x10ffff);
			Assert.InRange(ranges[index].End, ranges[index].Start, 0x10ffff);
			if (index != 0)
			{
				Assert.True(ranges[index - 1].End < ranges[index].Start,
					"Each source-derived range manifest must be exact, ordered, and internally non-overlapping.");
			}
		}
	}

	private static string RuneString(int scalar) => new Rune(scalar).ToString();

	private static string BuildImplementationManifest(Type type)
	{
		var rows = new List<string>
		{
			$"TYPE|{type.FullName}|{(int)type.Attributes}|{CustomAttributeManifest(type.CustomAttributes)}",
		};
		const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
			BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		foreach (FieldInfo field in type.GetFields(Declared).OrderBy(field => field.Name, StringComparer.Ordinal))
		{
			rows.Add(
				$"FIELD|{field.Name}|{TypeIdentity(field.FieldType)}|{(int)field.Attributes}|" +
				$"{ModifierManifest(field.GetRequiredCustomModifiers())}|" +
				$"{ModifierManifest(field.GetOptionalCustomModifiers())}|" +
				CustomAttributeManifest(field.CustomAttributes));
		}
		foreach (PropertyInfo property in type.GetProperties(Declared).OrderBy(property => property.Name, StringComparer.Ordinal))
		{
			rows.Add(
				$"PROPERTY|{property.Name}|{TypeIdentity(property.PropertyType)}|{(int)property.Attributes}|" +
				$"{ModifierManifest(property.GetRequiredCustomModifiers())}|" +
				$"{ModifierManifest(property.GetOptionalCustomModifiers())}|" +
				$"{CustomAttributeManifest(property.CustomAttributes)}|" +
				$"{property.GetMethod?.Name}|{property.SetMethod?.Name}");
		}

		MethodBase[] methods = type.GetConstructors(Declared).Cast<MethodBase>()
			.Concat(type.GetMethods(Declared))
			.OrderBy(MethodBaseIdentity, StringComparer.Ordinal)
			.ToArray();
		foreach (MethodBase method in methods)
		{
			MethodBody? body = method.GetMethodBody();
			rows.Add(
				$"METHOD|{MethodBaseIdentity(method)}|{(int)method.Attributes}|" +
				$"{(int)method.GetMethodImplementationFlags()}|{(int)method.CallingConvention}|" +
				CustomAttributeManifest(method.CustomAttributes));
			if (method is MethodInfo methodInfo)
			{
				rows.Add(
					$"RETURN|{TypeIdentity(methodInfo.ReturnType)}|" +
					$"{ModifierManifest(methodInfo.ReturnParameter.GetRequiredCustomModifiers())}|" +
					$"{ModifierManifest(methodInfo.ReturnParameter.GetOptionalCustomModifiers())}|" +
					CustomAttributeManifest(methodInfo.ReturnParameter.CustomAttributes));
			}
			foreach (ParameterInfo parameter in method.GetParameters())
			{
				rows.Add(
					$"PARAM|{parameter.Position}|{parameter.Name}|{TypeIdentity(parameter.ParameterType)}|" +
					$"{(int)parameter.Attributes}|{ModifierManifest(parameter.GetRequiredCustomModifiers())}|" +
					$"{ModifierManifest(parameter.GetOptionalCustomModifiers())}|" +
					CustomAttributeManifest(parameter.CustomAttributes));
			}
			if (body is null)
			{
				rows.Add("BODY|null");
				continue;
			}

			rows.Add(
				$"BODY|{body.InitLocals}|{body.MaxStackSize}|" +
				Convert.ToHexString(body.GetILAsByteArray() ?? []).ToLowerInvariant());
			foreach (LocalVariableInfo local in body.LocalVariables)
			{
				rows.Add($"LOCAL|{local.LocalIndex}|{TypeIdentity(local.LocalType)}|{local.IsPinned}");
			}
			foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
			{
				int filterOffset = clause.Flags == ExceptionHandlingClauseOptions.Filter
					? clause.FilterOffset
					: -1;
				Type? catchType = clause.Flags == ExceptionHandlingClauseOptions.Clause
					? clause.CatchType
					: null;
				rows.Add(
					$"EH|{(int)clause.Flags}|{clause.TryOffset}|{clause.TryLength}|" +
					$"{clause.HandlerOffset}|{clause.HandlerLength}|{filterOffset}|" +
					TypeIdentity(catchType));
			}
			foreach (MemberInfo reference in GetIlReferences(method))
			{
				rows.Add($"REF|{ResolvedMemberIdentity(reference)}");
			}
			foreach (string literal in GetIlStringLiterals(method))
			{
				rows.Add($"STRING|{StringLiteralIdentity(literal)}");
			}
			foreach (byte[] signature in GetIlSignatures(method))
			{
				rows.Add($"SIGNATURE|{Convert.ToHexString(signature).ToLowerInvariant()}");
			}
		}
		return string.Join('\n', rows) + "\n";
	}

	private static string BuildAssemblyReferenceManifest(Assembly assembly)
	{
		string[] rows = assembly.GetReferencedAssemblies()
			.Select(reference =>
			{
				Version? normalizedVersion = reference.Version;
				if (reference.Name is "WalletWasabi" or "WalletWasabi.Client" or
					"WalletWasabi.Coordinator" or "WalletWasabi.Fluent")
				{
					Assert.Equal(typeof(LiquidWalletState).Assembly.GetName().Version, reference.Version);
					Assert.True(string.IsNullOrEmpty(reference.CultureName));
					Assert.Empty(reference.GetPublicKeyToken() ?? []);
					Assert.Equal(AssemblyNameFlags.None, reference.Flags);
					Assert.Equal(AssemblyContentType.Default, reference.ContentType);
					normalizedVersion = new Version(1, 0, 0, 0);
				}
				string token = Convert.ToHexString(reference.GetPublicKeyToken() ?? []).ToLowerInvariant();
				return $"{reference.Name}|{normalizedVersion}|{reference.CultureName ?? ""}|{token}|" +
					$"{(int)reference.Flags}|{(int)reference.ContentType}";
			})
			.OrderBy(value => value, StringComparer.Ordinal)
			.ToArray();
		return string.Join('\n', rows) + "\n";
	}

	private static string Sha256Utf8(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

	private static string CustomAttributeManifest(IEnumerable<CustomAttributeData> attributes) =>
		string.Join(",", attributes
			.Select(attribute =>
				$"{TypeIdentity(attribute.AttributeType)}({string.Join(";", attribute.ConstructorArguments.Select(CustomAttributeValue))})" +
				$"[{string.Join(";", attribute.NamedArguments.Select(argument => $"{argument.MemberName}={CustomAttributeValue(argument.TypedValue)}"))}]")
			.OrderBy(value => value, StringComparer.Ordinal));

	private static string CustomAttributeValue(CustomAttributeTypedArgument argument)
	{
		if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> values)
		{
			return $"[{string.Join(",", values.Select(CustomAttributeValue))}]";
		}
		return $"{TypeIdentity(argument.ArgumentType)}:{argument.Value}";
	}

	private static string ModifierManifest(IEnumerable<Type> modifiers) =>
		string.Join(",", modifiers.Select(TypeIdentity));

	private static string TypeIdentity(Type? type) =>
		LiquidWalletStateTests.NormalizeProductAssemblyVersion(type?.AssemblyQualifiedName ?? "null");

	private static string MethodBaseIdentity(MethodBase method)
	{
		string parameters = string.Join(",", method.GetParameters()
			.Select(parameter => TypeIdentity(parameter.ParameterType)));
		string genericArguments = method.IsGenericMethod
			? $"<{string.Join(",", method.GetGenericArguments().Select(TypeIdentity))}>"
			: "";
		string returnType = method is MethodInfo info ? TypeIdentity(info.ReturnType) : "void";
		return $"{TypeIdentity(method.DeclaringType)}::{method.Name}{genericArguments}({parameters})->{returnType}";
	}

	private static string ResolvedMemberIdentity(MemberInfo member) => member switch
	{
		MethodBase method => MethodBaseIdentity(method),
		FieldInfo field => $"{TypeIdentity(field.DeclaringType)}::{field.Name}:{TypeIdentity(field.FieldType)}",
		Type type => TypeIdentity(type),
		_ => $"{TypeIdentity(member.DeclaringType)}::{member.Name}",
	};

	private static string ApprovedReferenceKey(MemberInfo member) => member switch
	{
		MethodBase method =>
			$"M|{method.DeclaringType?.FullName}|{method.Name}|" +
			$"{string.Join(",", method.IsGenericMethod ? method.GetGenericArguments().Select(type => type.FullName) : [])}|" +
			$"{string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.FullName))}|" +
			$"{(method is MethodInfo info ? info.ReturnType.FullName : "void")}",
		FieldInfo field => $"F|{field.DeclaringType?.FullName}|{field.Name}|{field.FieldType.FullName}",
		Type type => $"T|{type.FullName}",
		_ => $"O|{member.DeclaringType?.FullName}|{member.Name}",
	};

	private static string StringLiteralIdentity(string value)
	{
		var identity = new StringBuilder(value.Length * 4);
		foreach (char codeUnit in value)
		{
			identity.Append(((int)codeUnit).ToString("X4", CultureInfo.InvariantCulture));
		}
		return $"{value.Length}:{identity}";
	}

	private static string MethodSignature(MethodInfo method)
	{
		string parameters = string.Join(",", method.GetParameters()
			.Select(parameter => parameter.ParameterType.ToString()));
		return $"{method.Name}({parameters})->{method.ReturnType}:" +
			$"{(method.IsStatic ? "static" : "instance")}";
	}

	private static bool IsForbiddenMember(MemberInfo member)
	{
		string identity = MemberIdentity(member);
		if (ForbiddenIdentityFragments.Any(fragment =>
			identity.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}
		if (member is MethodInfo methodInfo && IsForbiddenType(methodInfo.ReturnType))
		{
			return true;
		}
		if (member is MethodBase methodBase &&
			methodBase.GetParameters().Any(parameter => IsForbiddenType(parameter.ParameterType)))
		{
			return true;
		}
		if (member is MethodInfo genericMethod && genericMethod.IsGenericMethod &&
			genericMethod.GetGenericArguments().Any(IsForbiddenType))
		{
			return true;
		}
		if (member is FieldInfo field && IsForbiddenType(field.FieldType))
		{
			return true;
		}
		if (member is Type referencedType && IsForbiddenType(referencedType))
		{
			return true;
		}

		if (member.DeclaringType == typeof(string) && member.Name is
			"Normalize" or "Trim" or "TrimStart" or "TrimEnd" or
			"ToLower" or "ToLowerInvariant" or "ToUpper" or "ToUpperInvariant" or
			"Compare" or "CompareTo" or "Equals" or "Contains" or "StartsWith" or
			"EndsWith" or "IndexOf" or "LastIndexOf")
		{
			return true;
		}
		if ((member.DeclaringType == typeof(char) || member.DeclaringType == typeof(Rune)) &&
			(member.Name.StartsWith("ToUpper", StringComparison.Ordinal) ||
			 member.Name.StartsWith("ToLower", StringComparison.Ordinal)))
		{
			return true;
		}
		if (member.Name == nameof(IComparable.CompareTo) &&
			(member.DeclaringType == typeof(IComparable) ||
			 member.DeclaringType == typeof(IComparable<string>)))
		{
			return true;
		}
		if (member is MethodBase method &&
			method.GetParameters().Any(parameter => parameter.ParameterType == typeof(StringComparison)))
		{
			return true;
		}
		if (member.Name is "GetUnicodeCategory" or "IsWhiteSpace" ||
			(member.Name == "GetEnumerator" &&
			 member.DeclaringType?.FullName?.Contains("RuneEnumerator", StringComparison.Ordinal) != true))
		{
			return true;
		}
		if (member.DeclaringType?.Namespace == "System.Linq")
		{
			return true;
		}
		if (member.DeclaringType == typeof(StringComparer) && member.Name.Contains("Culture", StringComparison.Ordinal))
		{
			return true;
		}
		if (member.Name == "get_Default" && member.DeclaringType?.IsGenericType == true)
		{
			Type definition = member.DeclaringType.GetGenericTypeDefinition();
			if ((definition == typeof(Comparer<>) || definition == typeof(EqualityComparer<>)) &&
				member.DeclaringType.GetGenericArguments() is [Type argument] && argument == typeof(string))
			{
				return true;
			}
		}
		if (UsesImplicitStringComparer(member))
		{
			return true;
		}

		return false;
	}

	private static readonly string[] ForbiddenIdentityFragments =
	[
		"WalletWasabi.Blockchain.Analysis.Clustering.LabelsArray",
		"WalletWasabi.Liquid.Addresses",
		"WalletWasabi.Liquid.Native",
		"WalletWasabi.Liquid.Network",
		"WalletWasabi.Liquid.Rpc",
		"WalletWasabi.Liquid.Transactions",
		"WalletWasabi.Liquid.Wallet.LiquidWalletState",
		"WalletWasabi.Liquid.Wallet.LiquidOwnedOutput",
		"Pset",
		"Signing",
		"Persistence",
		"System.IO.",
		"System.Net.",
		"System.Diagnostics.",
		"System.Console",
		"System.Runtime.InteropServices.",
		"System.Reflection.",
		"System.Dynamic.",
		"System.Linq.Expressions.",
		"System.Delegate",
		"System.MulticastDelegate",
		"System.Activator",
		"System.Type",
		"Microsoft.CSharp.RuntimeBinder",
		"System.Collections.Comparer",
		"System.Collections.CaseInsensitive",
		"System.Globalization.CompareInfo",
		"System.Globalization.CultureInfo",
		"System.Text.Json",
		"WalletWasabi.Logging",
		"Microsoft.Extensions.Logging",
		"OpenTelemetry",
		"Newtonsoft.Json",
		"Serilog",
		"NLog",
	];

	private static bool IsForbiddenType(Type? type)
	{
		return IsForbiddenType(type, new HashSet<Type>());
	}

	private static bool IsForbiddenType(Type? type, HashSet<Type> visited)
	{
		if (type is null || !visited.Add(type))
		{
			return false;
		}
		if (type.IsPointer || type.IsFunctionPointer)
		{
			return true;
		}
		if (typeof(Delegate).IsAssignableFrom(type))
		{
			return true;
		}
		if (type.HasElementType)
		{
			return IsForbiddenType(type.GetElementType(), visited);
		}
		if (ForbiddenIdentityFragments.Any(fragment =>
			(type.FullName ?? type.Name).Contains(fragment, StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}
		return type.IsGenericType && type.GetGenericArguments().Any(argument => IsForbiddenType(argument, visited));
	}

	private static bool ContainsForbiddenModifiers(ParameterInfo parameter) =>
		parameter.GetRequiredCustomModifiers().Any(IsForbiddenType) ||
		parameter.GetOptionalCustomModifiers().Any(IsForbiddenType);

	private static bool UsesImplicitStringComparer(MemberInfo member)
	{
		if (member is not MethodBase method || member.DeclaringType is not Type declaringType)
		{
			return false;
		}
		if (declaringType == typeof(Array) && method.Name is "Sort" or "BinarySearch")
		{
			return !method.GetParameters().Any(parameter => IsExplicitStringComparer(parameter.ParameterType));
		}

		if (method.Name is "Sort" or "BinarySearch" &&
			(declaringType.GetGenericArguments().Contains(typeof(string)) ||
			 method.GetGenericArguments().Contains(typeof(string))))
		{
			return !method.GetParameters().Any(parameter => IsExplicitStringComparer(parameter.ParameterType));
		}

		if (!declaringType.IsGenericType)
		{
			return false;
		}

		Type definition = declaringType.GetGenericTypeDefinition();
		Type[] arguments = declaringType.GetGenericArguments();
		bool sortedStringCollection =
			(definition == typeof(SortedSet<>) && arguments is [Type setElement] && setElement == typeof(string)) ||
			((definition == typeof(SortedDictionary<,>) || definition == typeof(SortedList<,>)) &&
				arguments.Length == 2 && arguments[0] == typeof(string));
		return sortedStringCollection && method.IsConstructor &&
			!method.GetParameters().Any(parameter => IsExplicitStringComparer(parameter.ParameterType));
	}

	private static bool IsExplicitStringComparer(Type type) =>
		type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IComparer<>) &&
			type.GetGenericArguments() is [Type argument] && argument == typeof(string);

	private static bool IsForbiddenAttribute(Type type) =>
		new[]
		{
			"Debugger", "Serializable", "OnSerializ", "OnDeserializ", "DataContract",
			"DataMember", "Json", "Xml", "Yaml", "MessagePack", "Proto",
		}
			.Any(fragment =>
				(type.FullName ?? type.Name).Contains(fragment, StringComparison.OrdinalIgnoreCase));

	private static bool IsForbiddenAttribute(CustomAttributeData attribute) =>
		IsForbiddenAttribute(attribute.AttributeType) ||
		IsForbiddenType(attribute.AttributeType) ||
		attribute.ConstructorArguments.Any(IsForbiddenAttributeArgument) ||
		attribute.NamedArguments.Any(argument => IsForbiddenAttributeArgument(argument.TypedValue));

	private static bool IsForbiddenAttributeArgument(CustomAttributeTypedArgument argument)
	{
		if (argument.Value is Type type)
		{
			return IsForbiddenType(type);
		}
		return argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> values &&
			values.Any(IsForbiddenAttributeArgument);
	}

	private static bool IsRequiredEmptyBackingFieldAttribute(
		FieldInfo field,
		CustomAttributeData attribute) =>
		field.Name == "<Empty>k__BackingField" &&
		attribute.AttributeType == typeof(System.Diagnostics.DebuggerBrowsableAttribute) &&
		attribute.ConstructorArguments is [CustomAttributeTypedArgument argument] &&
		Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture) ==
			(int)System.Diagnostics.DebuggerBrowsableState.Never;

	private static string MemberIdentity(MemberInfo member) =>
		$"{member.Module.Assembly.GetName().Name}|{member.DeclaringType?.FullName}|{member.Name}";

	private static IEnumerable<MemberInfo> GetIlReferences(MethodBase method)
	{
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		Type[]? typeArguments = method.DeclaringType?.GetGenericArguments();
		Type[]? methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
		for (int position = 0; position < il.Length;)
		{
			OpCode opCode = ReadOpCode(il, ref position);
			int operandPosition = position;
			int operandSize = GetOperandSize(opCode.OperandType, il, operandPosition);
			if (opCode.OperandType is OperandType.InlineField or OperandType.InlineMethod or
				OperandType.InlineTok or OperandType.InlineType)
			{
				int token = BitConverter.ToInt32(il, operandPosition);
				MemberInfo? member = method.Module.ResolveMember(token, typeArguments, methodArguments);
				if (member is not null)
				{
					yield return member;
				}
			}
			position += operandSize;
		}
	}

	private static IReadOnlyList<OpCode> GetIlOpCodes(MethodBase method)
	{
		var opCodes = new List<OpCode>();
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		for (int position = 0; position < il.Length;)
		{
			OpCode opCode = ReadOpCode(il, ref position);
			opCodes.Add(opCode);
			position += GetOperandSize(opCode.OperandType, il, position);
		}
		return opCodes;
	}

	private static IReadOnlyList<string> GetIlStringLiterals(MethodBase method)
	{
		var literals = new List<string>();
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		for (int position = 0; position < il.Length;)
		{
			OpCode opCode = ReadOpCode(il, ref position);
			int operandPosition = position;
			int operandSize = GetOperandSize(opCode.OperandType, il, operandPosition);
			if (opCode.OperandType == OperandType.InlineString)
			{
				literals.Add(method.Module.ResolveString(BitConverter.ToInt32(il, operandPosition)));
			}
			position += operandSize;
		}
		return literals;
	}

	private static IReadOnlyList<byte[]> GetIlSignatures(MethodBase method)
	{
		var signatures = new List<byte[]>();
		byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
		for (int position = 0; position < il.Length;)
		{
			OpCode opCode = ReadOpCode(il, ref position);
			int operandPosition = position;
			int operandSize = GetOperandSize(opCode.OperandType, il, operandPosition);
			if (opCode.OperandType == OperandType.InlineSig)
			{
				signatures.Add(method.Module.ResolveSignature(BitConverter.ToInt32(il, operandPosition)));
			}
			position += operandSize;
		}
		return signatures;
	}

	private static OpCode ReadOpCode(byte[] il, ref int position)
	{
		byte first = il[position++];
		short value = first == 0xfe
			? (short)(0xfe00 | il[position++])
			: first;
		return OpCodeByValue[value];
	}

	private static int GetOperandSize(OperandType operandType, byte[] il, int position) =>
		operandType switch
		{
			OperandType.InlineNone => 0,
			OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
			OperandType.InlineVar => 2,
			OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or
				OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or
				OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
			OperandType.InlineI8 or OperandType.InlineR => 8,
			OperandType.InlineSwitch => sizeof(int) +
				(BitConverter.ToInt32(il, position) * sizeof(int)),
			_ => throw new InvalidOperationException($"Unsupported IL operand type {operandType}."),
		};

	private static readonly IReadOnlyDictionary<short, OpCode> OpCodeByValue = typeof(OpCodes)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(field => field.FieldType == typeof(OpCode))
		.Select(field => (OpCode)field.GetValue(null)!)
		.ToDictionary(opCode => opCode.Value);

	private readonly record struct ScalarRange(int Start, int End)
	{
		public bool Contains(int scalar) => scalar >= Start && scalar <= End;
	}

	[Flags]
	private enum DenyClass : byte
	{
		None = 0,
		Control = 1 << 0,
		WhiteSpace = 1 << 1,
		Format = 1 << 2,
		DefaultIgnorable = 1 << 3,
		Noncharacter = 1 << 4,
	}

	private sealed class AccessProbeReadOnlyList : IReadOnlyList<string>
	{
		private readonly string[] _values;
		private readonly int _reportedCount;

		public AccessProbeReadOnlyList(string[] values, int? reportedCount = null)
		{
			_values = values;
			_reportedCount = reportedCount ?? values.Length;
			IndexReads = new int[Math.Max(0, _reportedCount)];
		}

		public int Count
		{
			get
			{
				CountReads++;
				if (CountReads != 1)
				{
					throw new CallerOwnedCollectionException("Count was read more than once.");
				}
				return _reportedCount;
			}
		}

		public int CountReads { get; private set; }
		public int EnumeratorReads { get; private set; }
		public int[] IndexReads { get; }

		public string this[int index]
		{
			get
			{
				if ((uint)index >= (uint)_reportedCount || (uint)index >= (uint)_values.Length)
				{
					throw new CallerOwnedCollectionException("An unexpected index was read.");
				}
				IndexReads[index]++;
				if (IndexReads[index] != 1)
				{
					throw new CallerOwnedCollectionException("An index was read more than once.");
				}
				return _values[index];
			}
		}

		public IEnumerator<string> GetEnumerator()
		{
			EnumeratorReads++;
			throw new CallerOwnedCollectionException("Enumeration is forbidden.");
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class ThrowingReadOnlyList(
		CallerOwnedCollectionException failure,
		bool throwFromCount) : IReadOnlyList<string>
	{
		public int Count
		{
			get
			{
				CountReads++;
				return throwFromCount ? throw failure : 1;
			}
		}

		public string this[int index]
		{
			get
			{
				IndexReads++;
				throw failure;
			}
		}

		public int CountReads { get; private set; }
		public int IndexReads { get; private set; }
		public int EnumeratorReads { get; private set; }

		public IEnumerator<string> GetEnumerator()
		{
			EnumeratorReads++;
			throw failure;
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	private sealed class CallerOwnedCollectionException(string message) : Exception(message);
}
