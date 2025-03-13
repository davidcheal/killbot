using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using System.Drawing;

namespace Killbot
{
    public partial class App : Application
    {
        private NotifyIcon _notifyIcon;
        private bool _isMonitoring = true;
        private Thread _monitorThread;
        private Dictionary<string, ServiceInfo> _servicesToMonitor;
        private ContextMenuStrip _contextMenu;

        private class ServiceInfo
        {
            public bool IsMonitored { get; set; }
            public string DisplayName { get; set; }
            public ToolStripMenuItem MenuItem { get; set; }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                if (!EventLog.SourceExists("FUA"))
                {
                    EventLog.CreateEventSource("FUA", "Application");
                }

                EventLog.WriteEntry("FUA", "Service Monitor started successfully.", EventLogEntryType.Information);

                // Don't call base.OnStartup as it would create the main window
                InitializeServiceList();
                InitializeSystemTray();
                StartMonitoring();
            }
            catch (Exception ex)
            {
                EventLog.WriteEntry("FUA", $"Unhandled Exception: {ex}", EventLogEntryType.Error);
                System.Windows.MessageBox.Show($"Error: {ex.Message}", "Application Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        private void InitializeServiceList()
        {
            _servicesToMonitor = new Dictionary<string, ServiceInfo>
            {
                { "OneDrive Sync Host Service", new ServiceInfo { IsMonitored = false, DisplayName = "OneDrive" } },
                { "Print Spooler", new ServiceInfo { IsMonitored = false, DisplayName = "Print Spooler" } },
                { "Windows Search", new ServiceInfo { IsMonitored = false, DisplayName = "Windows Search" } }
                // Add more services here as needed
            };
        }

        private void InitializeSystemTray()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = new System.Drawing.Icon("resources/killbot.ico"),
                Visible = true,
                Text = "Service Monitor (Active)"
            };

            _contextMenu = new ContextMenuStrip();

            var toggleMonitoringItem = new ToolStripMenuItem("Pause Monitoring", null, ToggleMonitoring)
            {
                Image = _isMonitoring ? SystemIcons.Shield.ToBitmap() : SystemIcons.Error.ToBitmap()
            };
            _contextMenu.Items.Add(toggleMonitoringItem);
            _contextMenu.Items.Add(new ToolStripSeparator());

            foreach (var kvp in _servicesToMonitor)
            {
                var serviceKey = kvp.Key;
                var serviceInfo = kvp.Value;

                var menuItem = new ToolStripMenuItem(serviceInfo.DisplayName)
                {
                    Checked = serviceInfo.IsMonitored,
                    CheckOnClick = true,
                    ToolTipText = GetServiceStatus(serviceKey)
                };

                menuItem.CheckedChanged += (sender, args) =>
                {
                    serviceInfo.IsMonitored = menuItem.Checked;
                    UpdateServiceMenuItem(serviceKey, menuItem);
                };

                serviceInfo.MenuItem = menuItem;
                _contextMenu.Items.Add(menuItem);
                UpdateServiceMenuItem(serviceKey, menuItem);
            }

            _contextMenu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit", null, ExitApplication);
            _contextMenu.Items.Add(exitItem);

            _notifyIcon.ContextMenuStrip = _contextMenu;
            _notifyIcon.MouseClick += (sender, args) =>
            {
                if (args.Button == MouseButtons.Left)
                {
                    _contextMenu.Show(Cursor.Position);
                }
            };
        }

        private void StartMonitoring()
        {
            _monitorThread = new Thread(() =>
            {
                while (true)
                {
                    if (_isMonitoring)
                    {
                        foreach (var kvp in _servicesToMonitor.Where(s => s.Value.IsMonitored))
                        {
                            try
                            {
                                var serviceController = new ServiceController(kvp.Key);
                                if (serviceController.Status == ServiceControllerStatus.Running)
                                {
                                    EventLog.WriteEntry("FUA", $"Stopping service: {kvp.Key}", EventLogEntryType.Information);
                                    serviceController.Stop();
                                    serviceController.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                                }
                                UpdateServiceMenuItem(kvp.Key, kvp.Value.MenuItem);
                            }
                            catch (Exception ex)
                            {
                                EventLog.WriteEntry("FUA", $"Error controlling service {kvp.Key}: {ex.Message}", EventLogEntryType.Error);
                                Debug.WriteLine($"Error controlling service {kvp.Key}: {ex.Message}");
                            }
                        }
                    }

                    // Update service statuses in the menu
                    foreach (var kvp in _servicesToMonitor)
                    {
                        UpdateServiceMenuItem(kvp.Key, kvp.Value.MenuItem);
                    }

                    Thread.Sleep(2000);
                }
            })
            {
                IsBackground = true
            };
            _monitorThread.Start();
        }

        private string GetServiceStatus(string serviceName)
        {
            try
            {
                var service = new ServiceController(serviceName);
                return $"Status: {service.Status}";
            }
            catch (Exception)
            {
                return "Status: Unknown";
            }
        }

        private void UpdateServiceMenuItem(string serviceName, ToolStripMenuItem menuItem)
        {
            // Check if the menu item is part of a menu strip
            var parent = menuItem.GetCurrentParent() as ToolStrip;
            if (parent != null && parent.InvokeRequired)
            {
                parent.Invoke(new Action(() => UpdateServiceMenuItem(serviceName, menuItem)));
                return;
            }

            try
            {
                var service = new ServiceController(serviceName);
                var status = service.Status;
                
                menuItem.ToolTipText = $"Status: {status}";
                
                // Update the menu item appearance based on status
                if (_servicesToMonitor[serviceName].IsMonitored)
                {
                    menuItem.Image = status == ServiceControllerStatus.Stopped ? 
                        SystemIcons.Shield.ToBitmap() : SystemIcons.Warning.ToBitmap();
                }
                else
                {
                    menuItem.Image = null;
                }
            }
            catch (Exception)
            {
                menuItem.ToolTipText = "Status: Unknown";
                menuItem.Image = SystemIcons.Error.ToBitmap();
            }
        }

        private void ToggleMonitoring(object sender, EventArgs e)
        {
            _isMonitoring = !_isMonitoring;
            _notifyIcon.Text = _isMonitoring ? "Service Monitor (Active)" : "Service Monitor (Paused)";

            if (sender is ToolStripMenuItem menuItem)
            {
                menuItem.Text = _isMonitoring ? "Pause Monitoring" : "Resume Monitoring";
                menuItem.Image = _isMonitoring ? SystemIcons.Shield.ToBitmap() : SystemIcons.Error.ToBitmap();
            }

            EventLog.WriteEntry("FUA", _isMonitoring ? "Monitoring resumed" : "Monitoring paused", EventLogEntryType.Information);
        }

        private void ExitApplication(object sender, EventArgs e)
        {
            EventLog.WriteEntry("FUA", "Application shutting down", EventLogEntryType.Information);
            _notifyIcon.Visible = false;
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _notifyIcon?.Dispose();
            base.OnExit(e);
        }
    }
}
