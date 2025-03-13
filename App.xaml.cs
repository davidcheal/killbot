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
using System.Reflection;
using System.IO;

namespace Killbot
{
    public partial class App : Application
    {
        private const string Killbot = "Killbot";
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

        private Icon LoadIconFromResource()
        {
            try
            {
                EventLog.WriteEntry(Killbot, "Attempting to load application icon...", EventLogEntryType.Information);
                var assembly = Assembly.GetExecutingAssembly();
                var resourceNames = assembly.GetManifestResourceNames();
                EventLog.WriteEntry(Killbot, $"Available resources: {string.Join(", ", resourceNames)}", EventLogEntryType.Information);

                // Try to load the icon from the embedded resources
                var stream = assembly.GetManifestResourceStream("killbot.ico");
                if (stream != null)
                {
                    EventLog.WriteEntry(Killbot, "Successfully loaded icon from embedded resources", EventLogEntryType.Information);
                    return new Icon(stream);
                }

                // If that doesn't work, try to load from the application directory
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "killbot.ico");
                if (File.Exists(iconPath))
                {
                    EventLog.WriteEntry(Killbot, "Loading icon from application directory", EventLogEntryType.Information);
                    return new Icon(iconPath);
                }

                EventLog.WriteEntry(Killbot, "Failed to load custom icon, using system default", EventLogEntryType.Warning);
                return SystemIcons.Application;
            }
            catch (Exception ex)
            {
                EventLog.WriteEntry(Killbot, $"Error loading icon: {ex.Message}\nStack trace: {ex.StackTrace}", EventLogEntryType.Error);
                return SystemIcons.Application;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                if (!EventLog.SourceExists(Killbot))
                {
                    EventLog.WriteEntry("Application", "Creating new event source 'Killbot'", EventLogEntryType.Information);
                    EventLog.CreateEventSource(Killbot, "Application");
                }

                InitializeServiceList();
                InitializeSystemTray();
                StartMonitoring();
            }
            catch (Exception ex)
            {
                EventLog.WriteEntry(Killbot, $"Critical startup error: {ex.Message}\nStack trace: {ex.StackTrace}", EventLogEntryType.Error);
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
            try
            {
                EventLog.WriteEntry(Killbot, "Creating system tray icon...", EventLogEntryType.Information);
                _notifyIcon = new NotifyIcon
                {
                    Icon = LoadIconFromResource(),
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
                        EventLog.WriteEntry(Killbot, $"Service monitoring {(menuItem.Checked ? "enabled" : "disabled")} for {serviceKey}", EventLogEntryType.Information);
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
            catch (Exception ex)
            {
                EventLog.WriteEntry(Killbot, $"Error initializing system tray: {ex.Message}\nStack trace: {ex.StackTrace}", EventLogEntryType.Error);
                throw;
            }
        }

        private void StartMonitoring()
        {
            _monitorThread = new Thread(() =>
            {
                try
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
                                    var status = serviceController.Status;

                                    if (status == ServiceControllerStatus.Running)
                                    {

                                        serviceController.Stop();
                                        serviceController.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));

                                    }
                                    UpdateServiceMenuItem(kvp.Key, kvp.Value.MenuItem);
                                }
                                catch (Exception ex)
                                {
                                    EventLog.WriteEntry(Killbot, $"Error controlling service {kvp.Key}: {ex.Message}\nStack trace: {ex.StackTrace}", EventLogEntryType.Error);
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
                }
                catch (Exception ex)
                {
                    EventLog.WriteEntry(Killbot, $"Critical error in monitoring thread: {ex.Message}\nStack trace: {ex.StackTrace}", EventLogEntryType.Error);
                    throw;
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


        }

        private void ExitApplication(object sender, EventArgs e)
        {

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
