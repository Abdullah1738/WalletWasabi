using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

namespace WalletWasabi.Tests.Helpers;

internal static class RoslynFreshChildHarness
{
	internal static string CompileChildAssembly(
		string source,
		string childDirectoryName,
		string childFileName,
		IEnumerable<string>? additionalReferencePaths = null)
	{
		var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
		var referencePaths = new HashSet<string>(StringComparer.Ordinal)
		{
			typeof(object).Assembly.Location,
			typeof(Console).Assembly.Location,
			typeof(JsonDocument).Assembly.Location,
			typeof(List<>).Assembly.Location,
			typeof(System.Buffers.ReadOnlySequence<>).Assembly.Location,
			typeof(WalletWasabi.Liquid.Wallet.LiquidWalletLoadSave).Assembly.Location,
			Assembly.Load("System.Runtime").Location,
		};
		if (additionalReferencePaths is not null)
		{
			referencePaths.UnionWith(additionalReferencePaths);
		}

		var compilation = CSharpCompilation.Create(
			"WalletWasabi.Tests",
			[syntaxTree],
			referencePaths.Select(path => MetadataReference.CreateFromFile(path)),
			new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release));
		string childDirectory = Path.Combine(AppContext.BaseDirectory, childDirectoryName);
		Directory.CreateDirectory(childDirectory);
		File.Copy(
			typeof(WalletWasabi.Liquid.Wallet.LiquidWalletLoadSave).Assembly.Location,
			Path.Combine(childDirectory, "WalletWasabi.dll"),
			overwrite: true);
		foreach (string moduleDependency in new[]
		{
			"NBitcoin.dll",
			"NBitcoin.Secp256k1.dll",
			"Microsoft.Extensions.Logging.Abstractions.dll",
		})
		{
			string dependencyPath = Path.Combine(AppContext.BaseDirectory, moduleDependency);
			if (File.Exists(dependencyPath))
			{
				File.Copy(dependencyPath, Path.Combine(childDirectory, moduleDependency), overwrite: true);
			}
		}

		string childPath = Path.Combine(childDirectory, childFileName);
		using FileStream stream = File.Create(childPath);
		EmitResult emitted = compilation.Emit(stream);
		Assert.True(
			emitted.Success,
			string.Join("\n", emitted.Diagnostics
				.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
				.Select(diagnostic => diagnostic.ToString())));
		return childPath;
	}

	internal static JsonDocument RunChild(string childAssemblyPath, object inputPayload)
	{
		string runtimeConfigPath = Path.Combine(AppContext.BaseDirectory, "WalletWasabi.Tests.runtimeconfig.json");
		Assert.True(File.Exists(runtimeConfigPath), "The test runtime configuration is missing.");
		var startInfo = new ProcessStartInfo
		{
			FileName = ResolveDotnetHostPath(),
			WorkingDirectory = AppContext.BaseDirectory,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		startInfo.ArgumentList.Add("exec");
		startInfo.ArgumentList.Add("--runtimeconfig");
		startInfo.ArgumentList.Add(runtimeConfigPath);
		startInfo.ArgumentList.Add(childAssemblyPath);

		using var process = new Process { StartInfo = startInfo };
		Assert.True(process.Start(), "The fresh child process did not start.");
		process.StandardInput.Write(JsonSerializer.Serialize(inputPayload));
		process.StandardInput.Close();
		Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
		Task<string> errorTask = process.StandardError.ReadToEndAsync();
		if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
		{
			process.Kill(entireProcessTree: true);
			throw new Xunit.Sdk.XunitException("The fresh child process timed out.");
		}

		string error = errorTask.GetAwaiter().GetResult();
		string output = outputTask.GetAwaiter().GetResult();
		Assert.True(
			process.ExitCode == 0 && output.Length > 0,
			$"The fresh child process exited with code {process.ExitCode}. stderr: {error}");
		return JsonDocument.Parse(output);
	}

	private static string ResolveDotnetHostPath()
	{
		string? processPath = Environment.ProcessPath;
		if (processPath is not null && string.Equals(
			Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
		{
			return processPath;
		}

		string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
		if (!string.IsNullOrEmpty(dotnetRoot))
		{
			string candidate = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return "dotnet";
	}
}
