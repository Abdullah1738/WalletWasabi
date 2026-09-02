using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Liquid.Wallet.Ui;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Liquid;

/// <summary>
/// The Fluent display wrapper for one exact Liquid spend plan
/// (<see cref="LiquidWalletUiSpendPlan"/>): the plan's exact values plus the
/// pegged-aware display amounts on the explicit fee, the destinations, and
/// the per-asset selected totals. The plan's exactness, fail-closed ordering,
/// and canonical-txid semantics are untouched — this wrapper carries only
/// presentation projections. The conversion lives in the single
/// <see cref="Helpers.LiquidAmountDisplay"/> helper, so the strings are
/// unit-testable off the view.
/// </summary>
public sealed class LiquidSpendPlanItemViewModel : ViewModelBase
{
	public LiquidSpendPlanItemViewModel(UiContext uiContext, LiquidWalletUiSpendPlan plan)
		: base(uiContext)
	{
		ArgumentNullException.ThrowIfNull(plan);
		SelectedInputCount = plan.SelectedInputCount;
		ConfidentialOutputCount = plan.ConfidentialOutputCount;
		SourceRevision = plan.SourceRevision;
		ExplicitFee = new LiquidSpendPlanAssetAmountItemViewModel(uiContext, plan.ExplicitFee);
		Destinations = plan.Destinations
			.Select(destination => new LiquidSpendPlanDestinationItemViewModel(uiContext, destination))
			.ToList();
		SelectedTotals = plan.SelectedTotals
			.Select(amount => new LiquidSpendPlanAssetAmountItemViewModel(uiContext, amount))
			.ToList();
	}

	public int SelectedInputCount { get; }
	public int ConfidentialOutputCount { get; }
	public ulong SourceRevision { get; }
	public LiquidSpendPlanAssetAmountItemViewModel ExplicitFee { get; }
	public IReadOnlyList<LiquidSpendPlanDestinationItemViewModel> Destinations { get; }
	public IReadOnlyList<LiquidSpendPlanAssetAmountItemViewModel> SelectedTotals { get; }
}
