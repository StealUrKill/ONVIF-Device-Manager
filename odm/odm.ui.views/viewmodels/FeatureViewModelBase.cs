using System;
using System.ComponentModel;
using System.ServiceModel;
using Microsoft.FSharp.Control;
using Microsoft.Practices.Prism.Commands;
using Microsoft.Practices.Unity;
using odm.core;
using odm.ui.controls;
using odm.ui.core;
using utils;

namespace odm.ui.viewModels {
	/// <summary>Shared code for the pages that use the ONVIF features of onvif.utils.OnvifFeatures.</summary>
	public static class FeatureCalls {
		/// <summary>The message for the user. A camera that does not have the operation gives the "not supported" text.</summary>
		public static string Message(Exception err) {
			while (err.InnerException != null)
				err = err.InnerException;
			var fault = err as FaultException;
			if (err is NotSupportedException ||
				(fault != null && fault.Code != null && fault.Code.SubCode != null &&
				 (fault.Code.SubCode.Name == "ActionNotSupported" || (fault.Code.SubCode.SubCode != null && fault.Code.SubCode.SubCode.Name == "NoSuchService")))) {
				return FeatureStrings.instance.notSupported + Environment.NewLine + err.Message;
			}
			return err.Message;
		}

		/// <summary>Runs the call and gives the result on the UI thread.</summary>
		public static IDisposable Run<T>(FSharpAsync<T> call, Action<T> onSuccess, Action<Exception> onError) {
			return call.ObserveOnCurrentDispatcher().Subscribe(onSuccess, onError);
		}
	}

	/// <summary>A small base for item wrappers that the pages edit.</summary>
	public abstract class NotifyBase : INotifyPropertyChanged {
		public event PropertyChangedEventHandler PropertyChanged;
		protected void Notify(params string[] names) {
			var handler = PropertyChanged;
			if (handler == null)
				return;
			foreach (var name in names)
				handler(this, new PropertyChangedEventArgs(name));
		}
	}

	public abstract class FeatureChannelViewModel : ViewModelChannelBase {
		protected FeatureChannelViewModel(IUnityContainer container) : base(container) { }
		public FeatureStrings Strings { get { return FeatureStrings.instance; } }
		public LocalDevice AppStrings { get { return LocalDevice.instance; } }
		protected INvtSession session;

		public override void Load(INvtSession session, String chanToken, string profileToken, Account account, IVideoInfo videoInfo) {
			this.session = session;
			Reload();
		}
		protected abstract void Reload();

		/// <summary>Shows the error. The button goes back to the page, or loads it again if nothing is loaded.</summary>
		protected void ShowError(Exception err, bool reloadOnClose) {
			dbg.Error(err);
			ErrorMessage = FeatureCalls.Message(err);
			ErrorBtnClick = new DelegateCommand(() => {
				if (reloadOnClose) Reload(); else Current = States.Common;
			});
			Current = States.Error;
		}
	}

	public abstract class FeatureDeviceViewModel : ViewModelDeviceBase {
		protected FeatureDeviceViewModel(IUnityContainer container) : base(container) { }
		public FeatureStrings Strings { get { return FeatureStrings.instance; } }
		public LocalDevice AppStrings { get { return LocalDevice.instance; } }
		protected INvtSession session;

		public override void Load(INvtSession session, Account account) {
			this.session = session;
			CurrentAccount = account;
			Reload();
		}
		protected abstract void Reload();

		protected void ShowError(Exception err, bool reloadOnClose) {
			dbg.Error(err);
			ErrorMessage = FeatureCalls.Message(err);
			ErrorBtnClick = new DelegateCommand(() => {
				if (reloadOnClose) Reload(); else Current = States.Common;
			});
			Current = States.Error;
		}
	}
}
