using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace WalletWasabi.Fluent.Views.AddWallet.Liquid;

public class LiquidAddWalletPageView : UserControl
{
	public LiquidAddWalletPageView()
	{
		InitializeComponent();
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
