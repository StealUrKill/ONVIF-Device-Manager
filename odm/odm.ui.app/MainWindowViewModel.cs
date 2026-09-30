using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;

using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Prism.Events;
using Microsoft.Practices.Unity;

using odm.controllers;
using odm.ui.viewModels;
using odm.ui.views;
using utils;

namespace odm.ui {
	public class MainWindowViewModel : DependencyObject {
		public MainWindowViewModel() {
			var version = DisplayVersion();
			this.CreateBinding(TitleProperty, odm.ui.controls.CommonApplicationStrings.instance, x => {
				return String.Format("{0} v{1}", x.applicationName, version);
			});
		}

		// The version from version.json, for example "3.1.0" or "3.1.0-dev" (no git hash).
		static string DisplayVersion() {
			var entry = System.Reflection.Assembly.GetEntryAssembly();
			var info = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
				entry, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
			if (info != null && !String.IsNullOrEmpty(info.InformationalVersion))
				return info.InformationalVersion.Split('+')[0];
			var ver = entry.GetName().Version;
			return String.Format("{0}.{1}.{2}", ver.Major, ver.Minor, ver.Build);
		}
		public string Title {
			get { return (string)GetValue(TitleProperty); }
			set { SetValue(TitleProperty, value); }
		}
		// Using a DependencyProperty as the backing store for Title.  This enables animation, styling, binding, etc...
		public static readonly DependencyProperty TitleProperty =
			DependencyProperty.Register("Title", typeof(string), typeof(MainWindowViewModel));

	}
}
