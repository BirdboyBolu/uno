﻿using System;
using Uno;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Microsoft.Toolkit.Uwp.UI.Lottie
{
	public partial class LottieVisualSource : DependencyObject, IAnimatedVisualSource
	{
		public static readonly DependencyProperty UriSourceProperty = DependencyProperty.Register(
			"UriSource", typeof(Uri), typeof(LottieVisualSource), new PropertyMetadata(default(Uri)));

		public Uri UriSource
		{
			get => (Uri)GetValue(UriSourceProperty);
			set => SetValue(UriSourceProperty, value);
		}

		public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
			"Options", typeof(LottieVisualOptions), typeof(LottieVisualSource), new PropertyMetadata(LottieVisualOptions.None));

		[NotImplemented]
		public LottieVisualOptions Options
		{
			get => (LottieVisualOptions)GetValue(OptionsProperty);
			set => SetValue(OptionsProperty, value);
		}

		[NotImplemented]
		public static LottieVisualSource CreateFromString(string uri)
		{
			throw new NotImplementedException();
		}

#if !__WASM__
		public void Update(AnimatedVisualPlayer player)
		{
			throw new NotImplementedException();
		}

		public void Play(bool looped)
		{
			throw new NotImplementedException();
		}

		public void Stop()
		{
			throw new NotImplementedException();
		}

		public void Pause()
		{
			throw new NotImplementedException();
		}

		public void Resume()
		{
			throw new NotImplementedException();
		}

		public void Load()
		{
			throw new NotImplementedException();
		}

		public void Unload()
		{
			throw new NotImplementedException();
		}
#endif
	}

	[NotImplemented]
	public enum LottieVisualOptions
	{
		/// <summary>No options set.</summary>
		None = 0,

		/// <summary>
		/// Optimizes the translation of the Lottie so as to reduce resource
		/// usage during rendering. Note that this may slow down loading.
		/// </summary>
		[NotImplemented]
		Optimize = 1,

		/// <summary>
		/// Sets the AnimatedVisualPlayer.Diagnostics property with information
		/// about the Lottie.
		/// </summary>
		[NotImplemented]
		IncludeDiagnostics = 2,

		/// <summary>Enables all options.</summary>
		[NotImplemented]
		All = IncludeDiagnostics | Optimize,
	}
}
