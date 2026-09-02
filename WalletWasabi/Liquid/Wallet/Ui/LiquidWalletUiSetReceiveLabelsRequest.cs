using System;
using System.Collections.Generic;
using System.Linq;

namespace WalletWasabi.Liquid.Wallet.Ui;

/// <summary>
/// The public, non-secret request the Fluent Liquid receive surface hands to
/// the application composition layer to persist a durable label set bound to
/// the wallet's current next-receive derivation index. The label set is
/// keyed by the branch-0 receive index the open session's published
/// next-receive material carries; labels carry no key material. An empty
/// <see cref="Labels"/> clears the label for that index. The command runs
/// entirely inside the WalletWasabi assembly through the landed
/// generation-fenced receive-label command service; this request carries only
/// public immutable values.
/// </summary>
public sealed class LiquidWalletUiSetReceiveLabelsRequest
{
	public LiquidWalletUiSetReceiveLabelsRequest(
		string canonicalWalletId,
		IReadOnlyList<string> labels)
	{
		ArgumentException.ThrowIfNullOrEmpty(canonicalWalletId);
		ArgumentNullException.ThrowIfNull(labels);

		CanonicalWalletId = canonicalWalletId;
		Labels = labels.ToArray();
	}

	public string CanonicalWalletId { get; }

	/// <summary>
	/// The label set to bind to the current next-receive derivation index, as
	/// an immutable list of label strings (comma-separated suggestion labels
	/// split by the caller). An empty list removes the label.
	/// </summary>
	public IReadOnlyList<string> Labels { get; }
}
