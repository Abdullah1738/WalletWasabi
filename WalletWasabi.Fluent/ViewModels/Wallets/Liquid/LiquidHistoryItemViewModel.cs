using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using ReactiveUI;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The immutable Fluent projection of one public Liquid history row: the
/// redacted <see cref="TransactionReference"/> (display-only,
/// collision-tolerant, never an identity/key/lookup), explicit
/// <c>Confirmed at block height N</c> or <c>Unconfirmed</c> status text, the
/// asset-change item list, an explicit <c>No wallet balance change</c> text
/// for an empty change list, and the normal/private accessibility summaries.
/// No timestamp, confirmation count, fee, payment, address, label, outpoint,
/// full transaction id, or block hash is exposed. Status and credit/debit
/// are text, never color/icon alone.
/// </summary>
public sealed class LiquidHistoryItemViewModel : ViewModelBase
{
	private string _accessibilitySummary = "";

	public LiquidHistoryItemViewModel(UiContext uiContext, LiquidWalletUiHistoryRow row)
		: base(uiContext)
	{
		ArgumentNullException.ThrowIfNull(row);
		TransactionReference = row.TransactionReference;
		IsConfirmed = row.IsConfirmed;
		ConfirmationHeight = row.ConfirmationHeight;
		StatusText = row.IsConfirmed && row.ConfirmationHeight is { } height
			? $"Confirmed at block height {height}"
			: "Unconfirmed";
		HasBalanceChange = row.HasBalanceChange;
		EmptyChangeText = row.HasBalanceChange ? "" : "No wallet balance change";

		var changes = new LiquidHistoryAssetChangeItemViewModel[row.AssetChanges.Count];
		for (int index = 0; index < row.AssetChanges.Count; index++)
		{
			changes[index] = new LiquidHistoryAssetChangeItemViewModel(uiContext, row.AssetChanges[index]);
		}

		AssetChanges = new ReadOnlyCollection<LiquidHistoryAssetChangeItemViewModel>(changes);

		// The row automation name follows privacy mode: off, the redacted
		// reference and every asset credit/debit; on, exactly status plus
		// "Liquid transaction details hidden" with no reference, asset
		// identity, or amount. Driven by the landed UiConfig privacy flag so
		// the hidden values never enter the accessible subtree.
		PrivateAccessibilitySummary = $"{StatusText} Liquid transaction details hidden";
		_accessibilitySummary = NormalAccessibilitySummary;
		uiContext.Services.UiConfig
			.WhenAnyValue(config => config.PrivacyMode)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(privacyMode => AccessibilitySummary =
				privacyMode ? PrivateAccessibilitySummary : NormalAccessibilitySummary);
	}

	public string TransactionReference { get; }
	public bool IsConfirmed { get; }
	public uint? ConfirmationHeight { get; }
	public string StatusText { get; }
	public bool HasBalanceChange { get; }
	public string EmptyChangeText { get; }
	public IReadOnlyList<LiquidHistoryAssetChangeItemViewModel> AssetChanges { get; }

	/// <summary>
	/// The normal (privacy-mode-off) row automation name: explicit status
	/// plus the redacted transaction reference and every asset credit/debit
	/// in atomic units. Never a full transaction id or block hash.
	/// </summary>
	public string NormalAccessibilitySummary
	{
		get
		{
			var builder = new System.Text.StringBuilder(StatusText);
			builder.Append(' ').Append(TransactionReference);
			foreach (var change in AssetChanges)
			{
				builder
					.Append(' ')
					.Append(change.DirectionText)
					.Append(' ')
					.Append(change.NetAtomicUnits)
					.Append(' ')
					.Append(change.AssetDisplayReference);
			}

			if (!HasBalanceChange)
			{
				builder.Append(' ').Append(EmptyChangeText);
			}

			return builder.ToString();
		}
	}

	/// <summary>
	/// The privacy-mode-on row automation name: exactly status plus
	/// <c>Liquid transaction details hidden</c>. It contains no transaction
	/// reference, asset id/reference, amount, wallet path/name, or hidden
	/// child text.
	/// </summary>
	public string PrivateAccessibilitySummary { get; }

	/// <summary>
	/// The automation summary bound in the view: the normal summary when
	/// privacy mode is off, the privacy summary when it is on. Follows the
	/// landed UiConfig privacy flag.
	/// </summary>
	public string AccessibilitySummary
	{
		get => _accessibilitySummary;
		private set => this.RaiseAndSetIfChanged(ref _accessibilitySummary, value);
	}
}
