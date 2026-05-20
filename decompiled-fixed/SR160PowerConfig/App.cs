using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.XamlIl.Runtime;
using Avalonia.Themes.Fluent;
using CompiledAvaloniaXaml;

namespace SR160PowerConfig;

public class App : Application
{
	[CompilerGenerated]
	private static Action<object> _0021XamlIlPopulateOverride;

	public override void Initialize()
	{
		_0021XamlIlPopulateTrampoline(this);
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (base.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime classicDesktopStyleApplicationLifetime)
		{
			classicDesktopStyleApplicationLifetime.MainWindow = new MainWindow();
		}
		base.OnFrameworkInitializationCompleted();
	}

	[CompilerGenerated]
	private static void _0021XamlIlPopulate(IServiceProvider P_0, App P_1)
	{
		CompiledAvaloniaXaml.XamlIlContext.Context<App> context = new CompiledAvaloniaXaml.XamlIlContext.Context<App>(P_0, new object[1] { _0021AvaloniaResources.NamespaceInfo_003A_002FApp_002Eaxaml.Singleton }, "avares://SR160PowerConfig/App.axaml")
		{
			RootObject = P_1,
			IntermediateRoot = P_1
		};
		App app2;
		App app = (app2 = P_1);
		context.PushParent(app2);
		app2.Styles.Add(new FluentTheme(context));
		app2.Styles.Add(_0021AvaloniaResources.Build_003A_002FThemes_002FFluent_002Examl(XamlIlRuntimeHelpers.CreateRootServiceProviderV3(context)));
		context.PopParent();
		if (app is StyledElement styled)
		{
			NameScope.SetNameScope(styled, context.AvaloniaNameScope);
		}
		context.AvaloniaNameScope.Complete();
	}

	[CompilerGenerated]
	private static void _0021XamlIlPopulateTrampoline(App P_0)
	{
		if (_0021XamlIlPopulateOverride != null)
		{
			_0021XamlIlPopulateOverride(P_0);
		}
		else
		{
			_0021XamlIlPopulate(XamlIlRuntimeHelpers.CreateRootServiceProviderV3(null), P_0);
		}
	}
}
