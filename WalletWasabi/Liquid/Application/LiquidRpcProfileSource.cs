using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WalletWasabi.Liquid.Application;

internal sealed record LiquidRpcProfile(
	string Name,
	Uri Endpoint,
	string CookieFilePath,
	string Network,
	string Manifest,
	TimeSpan ConnectTimeout,
	TimeSpan RequestTimeout);

internal sealed class LiquidRpcProfileSource
{
	private const string Schema = "walletwasabi-liquid-rpc-profile/v1";
	private const int MaxProfileBytes = 16 * 1024;
	private readonly string _applicationDataDirectory;

	internal LiquidRpcProfileSource(string applicationDataDirectory)
	{
		_applicationDataDirectory = RequireCanonicalDirectory(applicationDataDirectory);
	}

	internal LiquidRpcProfile LoadValidated(string profileName)
	{
		if (string.IsNullOrWhiteSpace(profileName) || profileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
			throw new InvalidDataException("A valid RPC profile name is required.");

		string profileDirectory = Path.Combine(_applicationDataDirectory, "liquid-rpc-profiles");
		string profilePath = Path.Combine(profileDirectory, profileName + ".json");
		EnsureRegularNonLinkFile(profilePath, profileDirectory, requireOwnerOnly: true);
		if (new FileInfo(profilePath).Length > MaxProfileBytes)
			throw new InvalidDataException("The RPC profile is too large.");

		using FileStream stream = new(profilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
		using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
		JsonElement root = document.RootElement;
		string schema = RequiredString(root, "schema");
		string name = RequiredString(root, "name");
		if (!StringComparer.Ordinal.Equals(schema, Schema) || !StringComparer.Ordinal.Equals(name, profileName))
			throw new InvalidDataException("The RPC profile identity is invalid.");

		Uri endpoint = ParseEndpoint(RequiredString(root, "endpoint"));
		string cookieFile = RequirePathUnderRoot(RequiredString(root, "cookieFile"), _applicationDataDirectory);
		EnsureRegularNonLinkFile(cookieFile, _applicationDataDirectory, requireOwnerOnly: true);
		string network = RequiredString(root, "network");
		string manifest = RequiredString(root, "manifest");
		TimeSpan connectTimeout = ReadTimeout(root, "connectTimeoutMs", 1, 30_000);
		TimeSpan requestTimeout = ReadTimeout(root, "requestTimeoutMs", 1, 120_000);
		return new(name, endpoint, cookieFile, network, manifest, connectTimeout, requestTimeout);
	}

	private static Uri ParseEndpoint(string value)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint)
			|| endpoint.Scheme != Uri.UriSchemeHttp
			|| endpoint.UserInfo.Length != 0
			|| endpoint.Query.Length != 0
			|| endpoint.Fragment.Length != 0
			|| endpoint.Port is < 1 or > 65535
			|| endpoint.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
			throw new InvalidDataException("The RPC endpoint must be an HTTP loopback IP endpoint.");

		if (!IPAddress.TryParse(endpoint.Host, out IPAddress? address) || !IPAddress.IsLoopback(address))
			throw new InvalidDataException("The RPC endpoint must be loopback-only.");
		return endpoint;
	}

	private static string RequirePathUnderRoot(string path, string root)
	{
		if (!Path.IsPathFullyQualified(path))
			throw new InvalidDataException("The cookie path must be absolute.");
		string fullPath = Path.GetFullPath(path);
		string fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
		if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
			throw new InvalidDataException("The cookie path must remain under the application data directory.");
		return fullPath;
	}

	private static void EnsureRegularNonLinkFile(string path, string parentDirectory, bool requireOwnerOnly)
	{
		if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
			throw new InvalidDataException("The configured path must be an existing regular file.");
		string canonicalParent = Path.GetFullPath(parentDirectory);
		string actualParent = Path.GetFullPath(Directory.GetParent(path)!.FullName);
		if (!StringComparer.Ordinal.Equals(canonicalParent, actualParent))
			throw new InvalidDataException("The configured file parent is invalid.");
		if (requireOwnerOnly)
		{
			if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
			{
				UnixFileMode mode = File.GetUnixFileMode(path);
				if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
					UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
					throw new SecurityException("The configured file must not grant group or other permissions.");
			}
			else if (!OperatingSystem.IsWindows())
			{
				throw new PlatformNotSupportedException("The owner-only permission check is unavailable on this platform.");
			}
		}
	}

	private static string RequireCanonicalDirectory(string path)
	{
		if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
			throw new DirectoryNotFoundException(path);
		string fullPath = Path.GetFullPath(path);
		if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
			throw new SecurityException("The application data directory must not be a link.");
		return fullPath;
	}

	private static string RequiredString(JsonElement root, string property)
	{
		if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
			throw new InvalidDataException("The RPC profile is missing a required value.");
		return value.GetString()!;
	}

	private static TimeSpan ReadTimeout(JsonElement root, string property, int min, int max)
	{
		if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int milliseconds) || milliseconds < min || milliseconds > max)
			throw new InvalidDataException("The RPC profile timeout is invalid.");
		return TimeSpan.FromMilliseconds(milliseconds);
	}

	private static string EnsureTrailingSeparator(string path) =>
		path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}

internal sealed class LiquidRpcCookieCredentialSource
{
	private const int MaxCookieBytes = 4096;
	private readonly LiquidRpcProfile _profile;

	internal LiquidRpcCookieCredentialSource(LiquidRpcProfile profile) => _profile = profile ?? throw new ArgumentNullException(nameof(profile));
	internal Uri Endpoint => _profile.Endpoint;

	internal LiquidRpcAuthenticationLease Acquire()
	{
		LiquidRpcProfileSourceEnsureCookie(_profile.CookieFilePath);
		byte[] bytes = Array.Empty<byte>();
		try
		{
			using FileStream stream = new(_profile.CookieFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
			if (stream.Length is <= 0 or > MaxCookieBytes)
				throw new SecurityException("The RPC cookie size is invalid.");
			bytes = new byte[stream.Length];
			int read = 0;
			while (read < bytes.Length)
			{
				int count = stream.Read(bytes, read, bytes.Length - read);
				if (count == 0) break;
				read += count;
			}
			string value = new UTF8Encoding(false, true).GetString(bytes, 0, read);
			// Accept exactly one non-empty credential line with no terminator or exactly one
			// trailing LF or CRLF terminator (Elements writes the cookie with no trailing
			// newline). Any embedded or additional line terminator, extra blank line, or NUL
			// remains rejected.
			if (value.Length == 0 || value.IndexOf('\0') >= 0 || value.IndexOfAnyExcept('\n', '\r') < 0)
				throw new SecurityException("The RPC cookie must contain exactly one line.");
			string line = value;
			if (line.EndsWith('\n'))
			{
				line = line[..^1];
				if (line.EndsWith('\r'))
					line = line[..^1];
			}
			if (line.Length == 0 || line.IndexOfAny('\n', '\r') >= 0)
				throw new SecurityException("The RPC cookie must contain exactly one line.");
			int separator = line.IndexOf(':');
			if (separator <= 0 || separator == line.Length - 1 || line.IndexOf(':', separator + 1) >= 0)
				throw new SecurityException("The RPC cookie format is invalid.");
			return new LiquidRpcAuthenticationLease(line[..separator], line[(separator + 1)..]);
		}
		catch (DecoderFallbackException ex)
		{
			throw new SecurityException("The RPC cookie encoding is invalid.", ex);
		}
		finally
		{
			CryptographicOperations.ZeroMemory(bytes);
		}
	}

	private static void LiquidRpcProfileSourceEnsureCookie(string path)
	{
		if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
			throw new SecurityException("The RPC cookie must be a regular non-link file.");
		if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
		{
			UnixFileMode mode = File.GetUnixFileMode(path);
			if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
				UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
				throw new SecurityException("The RPC cookie must not grant group or other permissions.");
		}
		else if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException("The RPC cookie owner-only permission check is unavailable on this platform.");
		}
	}
}

internal sealed class LiquidRpcAuthenticationLease : IDisposable
{
	private char[]? _username;
	private char[]? _password;
	internal LiquidRpcAuthenticationLease(string username, string password)
	{
		_username = username.ToCharArray();
		_password = password.ToCharArray();
	}
	internal ReadOnlySpan<char> Username => _username ?? ReadOnlySpan<char>.Empty;
	internal ReadOnlySpan<char> Password => _password ?? ReadOnlySpan<char>.Empty;
	public void Dispose()
	{
		if (_username is not null) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_username.AsSpan()));
		if (_password is not null) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_password.AsSpan()));
		_username = null;
		_password = null;
	}
}
