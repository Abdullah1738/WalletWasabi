using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin.Secp256k1;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Groups;
using WabiSabi.Crypto.Randomness;
using WabiSabi.Crypto.ZeroKnowledge;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Liquid.CoinJoin;

public sealed class LiquidCoinJoinCredentialCompatibilityTests
{
	[Fact]
	public async Task IssuanceOpeningDoesNotOpenThePresentedCommitment()
	{
		var random = new SecureRandom();
		var key = new CredentialIssuerSecretKey(random);
		var issuer = new CredentialIssuer(key, random, 8191);
		var client = new WabiSabiClient(key.ComputeCredentialIssuerParameters(), random, 8191);
		var bootstrap = client.CreateRequestForZeroAmount();
		var response = await issuer.HandleRequestAsync(bootstrap.CredentialsRequest, CancellationToken.None);
		var credentials = client.HandleResponse(response, bootstrap.CredentialsResponseValidation).ToArray();
		var issuance = bootstrap.CredentialsRequest.Requested.ToArray();
		var openings = bootstrap.CredentialsResponseValidation.Requested.ToArray();

		// Zero credentials suffice to test the algebra without authorizing unbacked value.
		Assert.All(credentials, credential => Assert.Equal(0, credential.Value));
		var reissue = client.CreateRequest(new long[] { 0, 0 }, credentials, CancellationToken.None);
		var presentations = reissue.CredentialsRequest.Presented.ToArray();
		Assert.Equal(credentials.Length, presentations.Length);
		for (var index = 0; index < credentials.Length; index++)
		{
			var credential = credentials[index];
			var ma = new Scalar((ulong)credential.Value) * Generators.Gg + credential.Randomness * Generators.Gh;
			Assert.Equal(issuance[index].Ma, ma);
			Assert.Equal(openings[index].Ma, ma);
			Assert.Equal(openings[index].Randomness, credential.Randomness);
			// Ca adds z*Ga. Passing Ca and the available issuance randomness to ABI 8/9 is not valid.
			Assert.NotEqual(ma, presentations[index].Ca);
		}

		var reissued = await issuer.HandleRequestAsync(reissue.CredentialsRequest, CancellationToken.None);
		Assert.All(client.HandleResponse(reissued, reissue.CredentialsResponseValidation), credential => Assert.Equal(0, credential.Value));
	}

	[Fact]
	public void ShippedPublicValidationSurfaceDoesNotExposePresentationRandomizers()
	{
		Assert.Equal(new[] { "Presented", "Requested" }, PublicProperties(typeof(CredentialsResponseValidation)));
		Assert.Equal(new[] { "Ca", "CV", "Cx0", "Cx1", "S" }, PublicProperties(typeof(CredentialPresentation)));
		Assert.Equal(new[] { "Mac", "Randomness", "Value" }, PublicProperties(typeof(Credential)));
		Assert.Equal(new[] { "CredentialsRequest", "CredentialsResponseValidation" }, PublicProperties(typeof(RealCredentialsRequestData)));
	}

	private static string[] PublicProperties(Type type) => type.GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
}
