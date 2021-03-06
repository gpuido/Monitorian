﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

using Monitorian.Models;
using Monitorian.Models.Monitor;
using Monitorian.Models.Watcher;
using Monitorian.ViewModels;
using Monitorian.Views;
using ScreenFrame;

namespace Monitorian
{
	public class MainController
	{
		private readonly Application _current = Application.Current;

		public Settings Settings { get; }

		public ObservableCollection<MonitorViewModel> Monitors { get; }
		private readonly object _monitorsLock = new object();

		public NotifyIconContainer NotifyIconContainer { get; }

		private readonly SettingsWatcher _settingsWatcher;
		private readonly PowerWatcher _powerWatcher;
		private readonly BrightnessWatcher _brightnessWatcher;

		public MainController()
		{
			Settings = new Settings();

			Monitors = new ObservableCollection<MonitorViewModel>();
			BindingOperations.EnableCollectionSynchronization(Monitors, _monitorsLock);

			NotifyIconContainer = new NotifyIconContainer();
			NotifyIconContainer.MouseLeftButtonClick += OnMainWindowShowRequested;
			NotifyIconContainer.MouseRightButtonClick += OnMenuWindowShowRequested;

			_settingsWatcher = new SettingsWatcher();
			_powerWatcher = new PowerWatcher();
			_brightnessWatcher = new BrightnessWatcher();
		}

		internal async Task InitiateAsync(RemotingAgent agent)
		{
			if (agent == null)
				throw new ArgumentNullException(nameof(agent));

			Settings.Load();

			NotifyIconContainer.ShowIcon("pack://application:,,,/Resources/Icons/TrayIcon.ico", ProductInfo.Title);

			_current.MainWindow = new MainWindow(this);
			_current.MainWindow.Deactivated += OnMainWindowDeactivated;

			if (!StartupService.IsStartedOnSignIn(Settings.LastSaveTime))
				_current.MainWindow.Show();

			agent.ShowRequested += OnMainWindowShowRequested;

			await ScanAsync();

			_settingsWatcher.Subscribe(() => ScanAsync());
			_powerWatcher.Subscribe(() => ScanAsync());
			_brightnessWatcher.Subscribe((instanceName, brightness) => Update(instanceName, brightness));
		}

		internal void End()
		{
			MonitorsDispose();
			NotifyIconContainer.Dispose();

			Settings.Save();

			_settingsWatcher.Dispose();
			_powerWatcher.Dispose();
			_brightnessWatcher.Dispose();
		}

		private async void OnMainWindowShowRequested(object sender, EventArgs e)
		{
			ShowMainWindow();
			await UpdateAsync();
		}

		private void OnMenuWindowShowRequested(object sender, Point e)
		{
			ShowMenuWindow(e);
		}

		private void ShowMainWindow()
		{
			var window = (MainWindow)_current.MainWindow;
			if (!window.CanBeShown)
				return;

			if (window.Visibility == Visibility.Visible)
				return;

			window.Show();
			window.Activate();
		}

		private void ShowMenuWindow(Point pivot)
		{
			var window = new MenuWindow(this, pivot);
			window.ViewModel.CloseAppRequested += (sender, e) => _current.Shutdown();
			window.Deactivated += OnMenuWindowDeactivated;
			window.Show();
		}

		private void OnMainWindowDeactivated(object sender, EventArgs e)
		{
			if (StoreNames())
				Settings.Save();
		}

		private void OnMenuWindowDeactivated(object sender, EventArgs e)
		{
			Settings.Save();
		}

		#region Monitors

		internal event EventHandler<bool> ScanningChanged;

		private static readonly Lazy<int> _maxMonitorCount = new Lazy<int>(() =>
		{
			int count = 4;
			SetCount(ref count);
			return count;
		});

		private static readonly Lazy<int> _maxNameCount = new Lazy<int>(() => _maxMonitorCount.Value * 4);

		private int _scanCount = 0;
		private int _updateCount = 0;

		private async Task ScanAsync()
		{
			var isEntered = false;
			try
			{
				isEntered = (Interlocked.Increment(ref _scanCount) == 1);
				if (isEntered)
				{
					ScanningChanged?.Invoke(this, true);

					var scanTime = DateTimeOffset.Now;

					await Task.Run(() =>
					{
						var oldMonitors = Monitors.ToList();

						foreach (var item in MonitorManager.EnumerateMonitors())
						{
							var oldMonitor = oldMonitors.FirstOrDefault(x =>
								string.Equals(x.DeviceInstanceId, item.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
							if (oldMonitor != null)
							{
								oldMonitors.Remove(oldMonitor);
								item.Dispose();
								continue;
							}

							var newMonitor = new MonitorViewModel(item);
							RetrieveName(newMonitor);
							if (Monitors.Count < _maxMonitorCount.Value)
							{
								newMonitor.UpdateBrightness();
								newMonitor.IsTarget = true;
							}
							lock (_monitorsLock)
							{
								Monitors.Add(newMonitor);
							}
						}

						foreach (var oldMonitor in oldMonitors)
						{
							oldMonitor.Dispose();
							lock (_monitorsLock)
							{
								Monitors.Remove(oldMonitor);
							}
						}
					});

					await Task.WhenAll(Monitors
						.Take(_maxMonitorCount.Value)
						.Where(x => x.UpdateTime < scanTime)
						.Select(x => Task.Run(() =>
						{
							x.UpdateBrightness();
							x.IsTarget = true;
						})));
				}
			}
			finally
			{
				if (isEntered)
				{
					ScanningChanged?.Invoke(this, false);

					Interlocked.Exchange(ref _scanCount, 0);
				}
			}
		}

		private async Task UpdateAsync()
		{
			if (_scanCount > 0)
				return;

			var isEntered = false;
			try
			{
				isEntered = (Interlocked.Increment(ref _updateCount) == 1);
				if (isEntered)
				{
					await Task.WhenAll(Monitors
						.Where(x => x.IsTarget)
						.Select(x => Task.Run(() => x.UpdateBrightness())));
				}
			}
			finally
			{
				if (isEntered)
				{
					Interlocked.Exchange(ref _updateCount, 0);
				}
			}
		}

		private void Update(string instanceName, int brightness)
		{
			var monitor = Monitors.FirstOrDefault(x => instanceName.StartsWith(x.DeviceInstanceId, StringComparison.OrdinalIgnoreCase));
			if (monitor != null)
			{
				monitor.UpdateBrightness(brightness);
			}
		}

		private void MonitorsDispose()
		{
			foreach (var monitor in Monitors)
				monitor.Dispose();
		}

		private void RetrieveName(MonitorViewModel monitor)
		{
			if (Settings.KnownMonitors.TryGetValue(monitor.DeviceInstanceId, out NamePack knownMonitor))
			{
				monitor.RestoreName(knownMonitor.Name);
			}
		}

		private bool StoreNames()
		{
			var isNameChanged = false;

			foreach (var monitor in Monitors)
			{
				if (StoreName(monitor))
					isNameChanged = true;
			}

			if (isNameChanged)
			{
				TruncateNames();
				return true;
			}
			return false;
		}

		private bool StoreName(MonitorViewModel monitor)
		{
			if (!monitor.CheckNameChanged())
				return false;

			if (!Settings.KnownMonitors.ContainsKey(monitor.DeviceInstanceId))
			{
				if (monitor.HasName)
				{
					// Add
					Settings.KnownMonitors.Add(monitor.DeviceInstanceId, new NamePack(monitor.Name));
				}
			}
			else
			{
				if (monitor.HasName)
				{
					// Change
					Settings.KnownMonitors[monitor.DeviceInstanceId].Name = monitor.Name;
				}
				else
				{
					// Remove
					Settings.KnownMonitors.Remove(monitor.DeviceInstanceId);
				}
			}
			return true;
		}

		private void TruncateNames()
		{
			if (Settings.KnownMonitors.Count <= _maxNameCount.Value)
				return;

			foreach (var key in Settings.KnownMonitors
				.OrderByDescending(x => x.Value.Time)
				.Skip(_maxNameCount.Value)
				.Select(x => x.Key)
				.ToArray())
			{
				Settings.KnownMonitors.Remove(key);
			}
		}

		#endregion

		#region Configuration

		[Conditional("UNLIMITED")]
		private static void SetCount(ref int count)
		{
			count *= 8;
		}

		#endregion
	}
}