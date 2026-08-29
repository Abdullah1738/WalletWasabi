using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Liquid.Cryptography;
using WalletWasabi.Liquid.Network;
using ValidatedLiquidAddress = (string NetworkManifestId, WalletWasabi.Liquid.Addresses.LiquidAddressKind Kind, byte? WitnessVersion, string CanonicalAddressText, string UnconfidentialAddressText, byte[] ScriptPubKey, WalletWasabi.Liquid.Cryptography.LiquidBlindingPublicKey? BlindingPublicKey);

namespace WalletWasabi.Liquid.Addresses;

internal static class LiquidAddressCodec
{
	private const int MaximumAddressLength = 256;
	private const int MaximumBech32Length = 90;
	private const string ChecksumAlphabet = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
	private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
	private const uint Bech32Constant = 1;
	private const uint Bech32mConstant = 0x2bc830a3;
	private const ulong Blech32Constant = 1;
	private const ulong Blech32mConstant = 0x455972a3350f7a1;
	private const ulong Blech32LowMask = 0x7fffffffffffff;
	private const uint Bech32Generator0 = 0x3b6a57b2;
	private const uint Bech32Generator1 = 0x26508e6d;
	private const uint Bech32Generator2 = 0x1ea119fa;
	private const uint Bech32Generator3 = 0x3d4233dd;
	private const uint Bech32Generator4 = 0x2a1462b3;
	private const ulong Blech32Generator0 = 0x7d52fba40bd886;
	private const ulong Blech32Generator1 = 0x5e8dbf1a03950c;
	private const ulong Blech32Generator2 = 0x1c3a3c74072a18;
	private const ulong Blech32Generator3 = 0x385d72fa0e5139;
	private const ulong Blech32Generator4 = 0x7093e5a608865b;

	internal static ValidatedLiquidAddress Parse(
		ElementsPublicNetworkManifest manifest,
		string encodedAddress)
	{
		RequireReviewedManifest(manifest);
		ArgumentNullException.ThrowIfNull(encodedAddress);
		ValidateCommonText(encodedAddress);

		if (TryRecognizeReviewedHrp(
			encodedAddress,
			out ElementsPublicNetworkManifest? encodedManifest,
			out bool confidential))
		{
			ValidatedLiquidAddress? parsed = TryParseWitness(encodedManifest, encodedAddress, confidential);
			if (parsed is null)
			{
				throw InvalidEncoding();
			}
			if (!ReferenceEquals(manifest, encodedManifest))
			{
				throw NetworkMismatch();
			}
			return parsed.Value;
		}

		ValidatedLiquidAddress? base58 = TryParseBase58(manifest, encodedAddress);
		if (base58 is not null)
		{
			return base58.Value;
		}

		foreach (ElementsPublicNetworkManifest otherManifest in OtherManifests(manifest))
		{
			if (!ReferenceEquals(otherManifest, manifest) && TryParseBase58(otherManifest, encodedAddress) is not null)
			{
				throw NetworkMismatch();
			}
		}

		throw InvalidEncoding();
	}

	internal static ValidatedLiquidAddress FromScriptPubKey(
		ElementsPublicNetworkManifest manifest,
		ReadOnlySpan<byte> scriptPubKey,
		LiquidBlindingPublicKey? blindingPublicKey)
	{
		RequireReviewedManifest(manifest);
		if (!TryRecognizeScript(
			scriptPubKey,
			out LiquidAddressKind kind,
			out byte? witnessVersion,
			out byte[] payload))
		{
			throw new ArgumentException(
				"The script cannot be represented by the reviewed Liquid address domain.",
				nameof(scriptPubKey));
		}

		ElementsAddressEncodingProfile profile = manifest.AddressEncoding;
		byte[]? blindingKey = blindingPublicKey?.GetCompressedPublicKey();
		string encodedAddress = witnessVersion is null
			? EncodeBase58Address(profile, kind, payload, blindingKey)
			: EncodeWitnessAddress(profile, witnessVersion.Value, payload, blindingKey);
		return Parse(manifest, encodedAddress);
	}

	private static ValidatedLiquidAddress? TryParseBase58(
		ElementsPublicNetworkManifest manifest,
		string encodedAddress)
	{
		if (!TryDecodeBase58Check(encodedAddress, out byte[] payload))
		{
			return null;
		}

		ElementsAddressEncodingProfile profile = manifest.AddressEncoding;
		byte[] hash;
		LiquidBlindingPublicKey? blindingPublicKey = null;
		if (payload.Length == 21 && TryGetBase58Kind(profile, payload[0], out var kind))
		{
			hash = payload[1..];
		}
		else if (
			payload.Length == 55 &&
			payload[0] == profile.ConfidentialPrefix &&
			TryGetBase58Kind(profile, payload[1], out kind))
		{
			hash = payload[35..];
			try
			{
				blindingPublicKey = LiquidBlindingPublicKey.Create(payload.AsSpan(2, 33));
			}
			catch (ArgumentException)
			{
				return null;
			}
		}
		else
		{
			return null;
		}

		string canonical = EncodeBase58Address(
			profile,
			kind,
			hash,
			blindingPublicKey?.GetCompressedPublicKey());
		if (!StringComparer.Ordinal.Equals(canonical, encodedAddress))
		{
			return null;
		}

		string unconfidential = EncodeBase58Address(profile, kind, hash, null);
		return new ValidatedLiquidAddress(
			manifest.ManifestId,
			kind,
			null,
			canonical,
			unconfidential,
			BuildScript(kind, null, hash),
			blindingPublicKey);
	}

	private static ValidatedLiquidAddress? TryParseWitness(
		ElementsPublicNetworkManifest manifest,
		string encodedAddress,
		bool confidential)
	{
		if (!IsWitnessTextEnvelopeValid(encodedAddress, confidential))
		{
			return null;
		}

		string canonicalInput = encodedAddress.ToLowerInvariant();
		ElementsAddressEncodingProfile profile = manifest.AddressEncoding;
		string expectedHrp = confidential ? profile.Blech32Hrp : profile.Bech32Hrp;
		int separator = canonicalInput.LastIndexOf('1');
		int checksumLength = confidential ? 12 : 6;
		if (
			separator != expectedHrp.Length ||
			!canonicalInput.AsSpan(0, separator).SequenceEqual(expectedHrp) ||
			canonicalInput.Length - separator - 1 < checksumLength + 1)
		{
			return null;
		}

		ReadOnlySpan<char> encodedData = canonicalInput.AsSpan(separator + 1);
		byte[] values = new byte[encodedData.Length];
		for (int index = 0; index < encodedData.Length; index++)
		{
			int value = ChecksumAlphabet.IndexOf(encodedData[index]);
			if (value < 0)
			{
				return null;
			}
			values[index] = (byte)value;
		}

		byte witnessVersion = values[0];
		if (witnessVersion > 16 || !HasExpectedChecksum(expectedHrp, values, confidential, witnessVersion))
		{
			return null;
		}

		if (!TryConvertBits(values.AsSpan(1, values.Length - checksumLength - 1), 5, 8, false, out byte[] decoded))
		{
			return null;
		}

		byte[] program;
		ReadOnlySpan<byte> publicKeyBytes = default;
		if (confidential)
		{
			if (decoded.Length < LiquidBlindingPublicKey.CompressedByteLength)
			{
				return null;
			}
			publicKeyBytes = decoded.AsSpan(0, LiquidBlindingPublicKey.CompressedByteLength);
			program = decoded[LiquidBlindingPublicKey.CompressedByteLength..];
		}
		else
		{
			program = decoded;
		}

		if (!IsValidWitnessProgram(witnessVersion, program.Length))
		{
			return null;
		}

		LiquidBlindingPublicKey? blindingPublicKey = null;
		if (confidential)
		{
			try
			{
				blindingPublicKey = LiquidBlindingPublicKey.Create(publicKeyBytes);
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		string canonical = EncodeWitnessAddress(
			profile,
			witnessVersion,
			program,
			blindingPublicKey?.GetCompressedPublicKey());
		if (!StringComparer.Ordinal.Equals(canonical, canonicalInput))
		{
			return null;
		}

		LiquidAddressKind kind = GetWitnessKind(witnessVersion, program.Length);
		return new ValidatedLiquidAddress(
			manifest.ManifestId,
			kind,
			witnessVersion,
			canonical,
			EncodeWitnessAddress(profile, witnessVersion, program, null),
			BuildScript(kind, witnessVersion, program),
			blindingPublicKey);
	}

	private static bool IsWitnessTextEnvelopeValid(string encodedAddress, bool confidential) =>
		!HasMixedCase(encodedAddress) &&
		(confidential || encodedAddress.Length <= MaximumBech32Length);

	private static string EncodeBase58Address(
		ElementsAddressEncodingProfile profile,
		LiquidAddressKind kind,
		ReadOnlySpan<byte> hash,
		byte[]? blindingPublicKey)
	{
		byte regularPrefix = kind switch
		{
			LiquidAddressKind.PayToPubKeyHash => profile.P2PkhPrefix,
			LiquidAddressKind.PayToScriptHash => profile.P2ShPrefix,
			_ => throw new InvalidOperationException("The address kind is not Base58Check encodable."),
		};

		byte[] payload;
		if (blindingPublicKey is null)
		{
			payload = new byte[21];
			payload[0] = regularPrefix;
			hash.CopyTo(payload.AsSpan(1));
		}
		else
		{
			payload = new byte[55];
			payload[0] = profile.ConfidentialPrefix;
			payload[1] = regularPrefix;
			blindingPublicKey.CopyTo(payload, 2);
			hash.CopyTo(payload.AsSpan(35));
		}

		return EncodeBase58Check(payload);
	}

	private static string EncodeWitnessAddress(
		ElementsAddressEncodingProfile profile,
		byte witnessVersion,
		ReadOnlySpan<byte> program,
		byte[]? blindingPublicKey)
	{
		bool confidential = blindingPublicKey is not null;
		string hrp = confidential ? profile.Blech32Hrp : profile.Bech32Hrp;
		byte[] payload = confidential
			? Combine(blindingPublicKey!, program)
			: program.ToArray();
		if (!TryConvertBits(payload, 8, 5, true, out byte[] converted))
		{
			throw new InvalidOperationException("The address payload could not be converted.");
		}

		byte[] values = new byte[converted.Length + 1];
		values[0] = witnessVersion;
		converted.CopyTo(values, 1);
		int checksumLength = confidential ? 12 : 6;
		ulong polymod = confidential
			? ComputeBlech32Polymod(hrp, values, checksumLength) ^
				(witnessVersion == 0 ? Blech32Constant : Blech32mConstant)
			: ComputeBech32Polymod(hrp, values, checksumLength) ^
				(witnessVersion == 0 ? Bech32Constant : Bech32mConstant);

		var result = new StringBuilder(hrp.Length + 1 + values.Length + checksumLength);
		result.Append(hrp);
		result.Append('1');
		foreach (byte value in values)
		{
			result.Append(ChecksumAlphabet[value]);
		}
		for (int index = 0; index < checksumLength; index++)
		{
			int shift = 5 * (checksumLength - 1 - index);
			result.Append(ChecksumAlphabet[(int)((polymod >> shift) & 31)]);
		}
		return result.ToString();
	}

	private static bool HasExpectedChecksum(
		string hrp,
		ReadOnlySpan<byte> values,
		bool confidential,
		byte witnessVersion) =>
		confidential
			? ComputeBlech32Polymod(hrp, values, 0) ==
				(witnessVersion == 0 ? Blech32Constant : Blech32mConstant)
			: ComputeBech32Polymod(hrp, values, 0) ==
				(witnessVersion == 0 ? Bech32Constant : Bech32mConstant);

	private static uint ComputeBech32Polymod(
		string hrp,
		ReadOnlySpan<byte> values,
		int trailingZeroes)
	{
		uint checksum = 1;
		foreach (char character in hrp)
		{
			checksum = Bech32Step(checksum, character >> 5);
		}
		checksum = Bech32Step(checksum, 0);
		foreach (char character in hrp)
		{
			checksum = Bech32Step(checksum, character & 31);
		}
		foreach (byte value in values)
		{
			checksum = Bech32Step(checksum, value);
		}
		for (int index = 0; index < trailingZeroes; index++)
		{
			checksum = Bech32Step(checksum, 0);
		}
		return checksum;
	}

	private static uint Bech32Step(uint checksum, int value)
	{
		uint top = checksum >> 25;
		checksum = ((checksum & 0x1ffffff) << 5) ^ (uint)value;
		for (int index = 0; index < 5; index++)
		{
			if (((top >> index) & 1) != 0)
			{
				checksum ^= Bech32Generator(index);
			}
		}
		return checksum;
	}

	private static uint Bech32Generator(int index) => index switch
	{
		0 => Bech32Generator0,
		1 => Bech32Generator1,
		2 => Bech32Generator2,
		3 => Bech32Generator3,
		4 => Bech32Generator4,
		_ => throw new ArgumentOutOfRangeException(nameof(index)),
	};

	private static ulong ComputeBlech32Polymod(
		string hrp,
		ReadOnlySpan<byte> values,
		int trailingZeroes)
	{
		ulong checksum = 1;
		foreach (char character in hrp)
		{
			checksum = Blech32Step(checksum, character >> 5);
		}
		checksum = Blech32Step(checksum, 0);
		foreach (char character in hrp)
		{
			checksum = Blech32Step(checksum, character & 31);
		}
		foreach (byte value in values)
		{
			checksum = Blech32Step(checksum, value);
		}
		for (int index = 0; index < trailingZeroes; index++)
		{
			checksum = Blech32Step(checksum, 0);
		}
		return checksum;
	}

	private static ulong Blech32Step(ulong checksum, int value)
	{
		ulong top = checksum >> 55;
		checksum = ((checksum & Blech32LowMask) << 5) ^ (uint)value;
		for (int index = 0; index < 5; index++)
		{
			if (((top >> index) & 1) != 0)
			{
				checksum ^= Blech32Generator(index);
			}
		}
		return checksum;
	}

	private static ulong Blech32Generator(int index) => index switch
	{
		0 => Blech32Generator0,
		1 => Blech32Generator1,
		2 => Blech32Generator2,
		3 => Blech32Generator3,
		4 => Blech32Generator4,
		_ => throw new ArgumentOutOfRangeException(nameof(index)),
	};

	private static bool TryConvertBits(
		ReadOnlySpan<byte> source,
		int fromBits,
		int toBits,
		bool pad,
		out byte[] converted)
	{
		ulong accumulator = 0;
		int bitCount = 0;
		int maximumValue = (1 << toBits) - 1;
		ulong maximumAccumulator = (1UL << (fromBits + toBits - 1)) - 1;
		var result = new List<byte>((source.Length * fromBits + toBits - 1) / toBits);
		foreach (byte value in source)
		{
			if ((value >> fromBits) != 0)
			{
				converted = [];
				return false;
			}
			accumulator = ((accumulator << fromBits) | value) & maximumAccumulator;
			bitCount += fromBits;
			while (bitCount >= toBits)
			{
				bitCount -= toBits;
				result.Add((byte)((accumulator >> bitCount) & (uint)maximumValue));
			}
		}

		if (pad)
		{
			if (bitCount > 0)
			{
				result.Add((byte)((accumulator << (toBits - bitCount)) & (uint)maximumValue));
			}
		}
		else if (
			bitCount >= fromBits ||
			((accumulator << (toBits - bitCount)) & (uint)maximumValue) != 0)
		{
			converted = [];
			return false;
		}

		converted = [.. result];
		return true;
	}

	private static string EncodeBase58Check(ReadOnlySpan<byte> payload)
	{
		byte[] withChecksum = new byte[payload.Length + 4];
		payload.CopyTo(withChecksum);
		byte[] firstHash = SHA256.HashData(payload);
		byte[] secondHash = SHA256.HashData(firstHash);
		secondHash.AsSpan(0, 4).CopyTo(withChecksum.AsSpan(payload.Length));
		return EncodeBase58(withChecksum);
	}

	private static bool TryDecodeBase58Check(string text, out byte[] payload)
	{
		if (!TryDecodeBase58(text, out byte[] decoded) || decoded.Length < 5)
		{
			payload = [];
			return false;
		}

		ReadOnlySpan<byte> candidatePayload = decoded.AsSpan(0, decoded.Length - 4);
		byte[] firstHash = SHA256.HashData(candidatePayload);
		byte[] secondHash = SHA256.HashData(firstHash);
		if (!CryptographicOperations.FixedTimeEquals(secondHash.AsSpan(0, 4), decoded.AsSpan(decoded.Length - 4)))
		{
			payload = [];
			return false;
		}
		if (!StringComparer.Ordinal.Equals(EncodeBase58(decoded), text))
		{
			payload = [];
			return false;
		}

		payload = candidatePayload.ToArray();
		return true;
	}

	private static string EncodeBase58(ReadOnlySpan<byte> bytes)
	{
		int leadingZeroes = 0;
		while (leadingZeroes < bytes.Length && bytes[leadingZeroes] == 0)
		{
			leadingZeroes++;
		}

		byte[] digits = new byte[bytes.Length * 138 / 100 + 1];
		int digitCount = 0;
		for (int sourceIndex = leadingZeroes; sourceIndex < bytes.Length; sourceIndex++)
		{
			int carry = bytes[sourceIndex];
			int digitIndex = 0;
			for (; digitIndex < digitCount; digitIndex++)
			{
				carry += digits[digitIndex] << 8;
				digits[digitIndex] = (byte)(carry % 58);
				carry /= 58;
			}
			while (carry > 0)
			{
				digits[digitCount++] = (byte)(carry % 58);
				carry /= 58;
			}
		}

		var result = new StringBuilder(leadingZeroes + digitCount);
		result.Append('1', leadingZeroes);
		for (int index = digitCount - 1; index >= 0; index--)
		{
			result.Append(Base58Alphabet[digits[index]]);
		}
		return result.ToString();
	}

	private static bool TryDecodeBase58(string text, out byte[] decoded)
	{
		byte[] bytes = new byte[text.Length];
		int byteCount = 0;
		foreach (char character in text)
		{
			int carry = Base58Alphabet.IndexOf(character);
			if (carry < 0)
			{
				decoded = [];
				return false;
			}
			int byteIndex = 0;
			for (; byteIndex < byteCount; byteIndex++)
			{
				carry += bytes[byteIndex] * 58;
				bytes[byteIndex] = (byte)carry;
				carry >>= 8;
			}
			while (carry > 0)
			{
				bytes[byteCount++] = (byte)carry;
				carry >>= 8;
			}
		}

		int leadingZeroes = 0;
		while (leadingZeroes < text.Length && text[leadingZeroes] == '1')
		{
			leadingZeroes++;
		}
		decoded = new byte[leadingZeroes + byteCount];
		for (int index = 0; index < byteCount; index++)
		{
			decoded[decoded.Length - 1 - index] = bytes[index];
		}
		return true;
	}

	private static bool TryRecognizeScript(
		ReadOnlySpan<byte> script,
		out LiquidAddressKind kind,
		out byte? witnessVersion,
		out byte[] payload)
	{
		if (
			script.Length == 25 &&
			script[0] == 0x76 &&
			script[1] == 0xa9 &&
			script[2] == 0x14 &&
			script[23] == 0x88 &&
			script[24] == 0xac)
		{
			kind = LiquidAddressKind.PayToPubKeyHash;
			witnessVersion = null;
			payload = script[3..23].ToArray();
			return true;
		}
		if (
			script.Length == 23 &&
			script[0] == 0xa9 &&
			script[1] == 0x14 &&
			script[22] == 0x87)
		{
			kind = LiquidAddressKind.PayToScriptHash;
			witnessVersion = null;
			payload = script[2..22].ToArray();
			return true;
		}
		if (script.Length is 22 or 34 && script[0] == 0x00 && script[1] == script.Length - 2)
		{
			kind = script.Length == 22
				? LiquidAddressKind.WitnessV0KeyHash
				: LiquidAddressKind.WitnessV0ScriptHash;
			witnessVersion = 0;
			payload = script[2..].ToArray();
			return true;
		}
		if (
			script.Length >= 4 &&
			script[0] is >= 0x51 and <= 0x60 &&
			script[1] is >= 2 and <= 40 &&
			script.Length == script[1] + 2)
		{
			witnessVersion = (byte)(script[0] - 0x50);
			payload = script[2..].ToArray();
			kind = GetWitnessKind(witnessVersion.Value, payload.Length);
			return true;
		}

		kind = default;
		witnessVersion = null;
		payload = [];
		return false;
	}

	private static byte[] BuildScript(
		LiquidAddressKind kind,
		byte? witnessVersion,
		ReadOnlySpan<byte> payload)
	{
		if (kind == LiquidAddressKind.PayToPubKeyHash)
		{
			byte[] script = new byte[25];
			byte[] prefix = [0x76, 0xa9, 0x14];
			prefix.CopyTo(script, 0);
			payload.CopyTo(script.AsSpan(3));
			script[23] = 0x88;
			script[24] = 0xac;
			return script;
		}
		if (kind == LiquidAddressKind.PayToScriptHash)
		{
			byte[] script = new byte[23];
			script[0] = 0xa9;
			script[1] = 0x14;
			payload.CopyTo(script.AsSpan(2));
			script[22] = 0x87;
			return script;
		}

		byte version = witnessVersion ?? throw new InvalidOperationException("A witness version is required.");
		byte[] witnessScript = new byte[payload.Length + 2];
		witnessScript[0] = version == 0 ? (byte)0 : (byte)(0x50 + version);
		witnessScript[1] = (byte)payload.Length;
		payload.CopyTo(witnessScript.AsSpan(2));
		return witnessScript;
	}

	private static LiquidAddressKind GetWitnessKind(byte version, int programLength) =>
		version switch
		{
			0 when programLength == 20 => LiquidAddressKind.WitnessV0KeyHash,
			0 => LiquidAddressKind.WitnessV0ScriptHash,
			1 when programLength == 32 => LiquidAddressKind.WitnessV1Taproot,
			_ => LiquidAddressKind.WitnessUnknown,
		};

	private static bool IsValidWitnessProgram(byte version, int length) =>
		version == 0 ? length is 20 or 32 : length is >= 2 and <= 40;

	private static bool TryGetBase58Kind(
		ElementsAddressEncodingProfile profile,
		byte prefix,
		out LiquidAddressKind kind)
	{
		if (prefix == profile.P2PkhPrefix)
		{
			kind = LiquidAddressKind.PayToPubKeyHash;
			return true;
		}
		if (prefix == profile.P2ShPrefix)
		{
			kind = LiquidAddressKind.PayToScriptHash;
			return true;
		}
		kind = default;
		return false;
	}

	private static bool TryRecognizeReviewedHrp(
		string encodedAddress,
		out ElementsPublicNetworkManifest manifest,
		out bool confidential)
	{
		int separator = encodedAddress.LastIndexOf('1');
		if (separator <= 0)
		{
			manifest = null!;
			confidential = false;
			return false;
		}

		ReadOnlySpan<char> hrp = encodedAddress.AsSpan(0, separator);
		if (TryRecognizeReviewedHrp(hrp, ElementsPublicNetworkManifest.LiquidMainnet, out confidential))
		{
			manifest = ElementsPublicNetworkManifest.LiquidMainnet;
			return true;
		}
		if (TryRecognizeReviewedHrp(hrp, ElementsPublicNetworkManifest.LiquidTestnet, out confidential))
		{
			manifest = ElementsPublicNetworkManifest.LiquidTestnet;
			return true;
		}
		if (TryRecognizeReviewedHrp(hrp, ElementsPublicNetworkManifest.LiquidControlledRegtest, out confidential))
		{
			manifest = ElementsPublicNetworkManifest.LiquidControlledRegtest;
			return true;
		}

		manifest = null!;
		confidential = false;
		return false;
	}

	private static bool TryRecognizeReviewedHrp(
		ReadOnlySpan<char> hrp,
		ElementsPublicNetworkManifest reviewed,
		out bool confidential)
	{
		if (EqualsOrdinalIgnoreCase(hrp, reviewed.AddressEncoding.Bech32Hrp))
		{
			confidential = false;
			return true;
		}
		if (EqualsOrdinalIgnoreCase(hrp, reviewed.AddressEncoding.Blech32Hrp))
		{
			confidential = true;
			return true;
		}
		confidential = false;
		return false;
	}

	private static bool EqualsOrdinalIgnoreCase(ReadOnlySpan<char> left, string right) =>
		left.Equals(right, StringComparison.OrdinalIgnoreCase);

	private static bool HasMixedCase(string text)
	{
		bool lower = false;
		bool upper = false;
		foreach (char character in text)
		{
			lower |= character is >= 'a' and <= 'z';
			upper |= character is >= 'A' and <= 'Z';
		}
		return lower && upper;
	}

	private static void ValidateCommonText(string encodedAddress)
	{
		if (encodedAddress.Length is 0 or > MaximumAddressLength)
		{
			throw InvalidEncoding();
		}
		foreach (char character in encodedAddress)
		{
			if (character is < (char)33 or > (char)126)
			{
				throw InvalidEncoding();
			}
		}
	}

	private static void RequireReviewedManifest(ElementsPublicNetworkManifest manifest)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		if (
			!ReferenceEquals(manifest, ElementsPublicNetworkManifest.LiquidMainnet) &&
			!ReferenceEquals(manifest, ElementsPublicNetworkManifest.LiquidTestnet) &&
			!IsReviewedControlledRegtestManifest(manifest))
		{
			throw new ArgumentException(
				"A reviewed Liquid public-network manifest is required.",
				nameof(manifest));
		}
	}

	private static bool IsReviewedControlledRegtestManifest(ElementsPublicNetworkManifest manifest) =>
		ReferenceEquals(manifest, ElementsPublicNetworkManifest.LiquidControlledRegtest) &&
		manifest.AddressEncoding == new ElementsAddressEncodingProfile(235, 75, 4, "ert", "el");

	private static ElementsPublicNetworkManifest[] OtherManifests(ElementsPublicNetworkManifest manifest)
	{
		if (ReferenceEquals(manifest, ElementsPublicNetworkManifest.LiquidMainnet))
		{
			return [ElementsPublicNetworkManifest.LiquidTestnet, ElementsPublicNetworkManifest.LiquidControlledRegtest];
		}
		if (ReferenceEquals(manifest, ElementsPublicNetworkManifest.LiquidTestnet))
		{
			return [ElementsPublicNetworkManifest.LiquidMainnet, ElementsPublicNetworkManifest.LiquidControlledRegtest];
		}
		return [ElementsPublicNetworkManifest.LiquidMainnet, ElementsPublicNetworkManifest.LiquidTestnet];
	}

	private static byte[] Combine(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
	{
		byte[] result = new byte[first.Length + second.Length];
		first.CopyTo(result);
		second.CopyTo(result.AsSpan(first.Length));
		return result;
	}

	private static LiquidAddressFormatException InvalidEncoding() =>
		new(LiquidAddressParseFailure.InvalidEncoding);

	private static LiquidAddressFormatException NetworkMismatch() =>
		new(LiquidAddressParseFailure.NetworkMismatch);
}
