using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using Deceive.Properties;

namespace Deceive;

internal class MainController : ApplicationContext, IAppHost
{
    internal MainController()
    {
        TrayIcon = new NotifyIcon
        {
            Icon = Resources.DeceiveIcon,
            Visible = true,
            BalloonTipTitle = StartupHandler.DeceiveTitle,
            BalloonTipText = $"{AppInfo.Name} is currently masking your status. Right-click the tray icon for more options."
        };
        TrayIcon.ShowBalloonTip(5000);
        TrayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowWindow();
        };

        // Hidden control created on the UI thread, used to marshal work from the proxy threads.
        _ui = new Control();
        _ = _ui.Handle;

        Alerts.Attach(this);
        StoreWatcher.Start(this);
        TrayIcon.BalloonTipClicked += (_, _) => ShowWindow();

        AppSettings.Initialize();
        AppSettings.Reloaded += async (_, _) => await OnSettingsReloadedAsync();

        LoadStatus();
        UpdateTray();
    }

    private readonly Control _ui;
    private MainWindow? _window;

    /// <summary>The app window, if it has been opened.</summary>
    internal MainWindow? Window => _window;

    /// <summary>A small Windows notification from the tray icon. Clicking it opens the window.</summary>
    internal void ShowTrayNotice(string title, string text) => RunOnUi(() =>
    {
        try { TrayIcon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info); }
        catch (Exception e) { Trace.WriteLine("Tray notice failed: " + e.Message); }
    });

    /// <summary>Raised on the UI thread whenever status, enabled state or tray changes.</summary>
    public event EventHandler? StateChanged;

    internal void RunOnUi(Action action)
    {
        if (_ui.IsDisposed)
            return;
        if (_ui.InvokeRequired)
            _ui.BeginInvoke(action);
        else
            action();
    }

    internal void ShowWindow(bool activate = true) => RunOnUi(() =>
    {
        if (_window is null || _window.IsDisposed)
            _window = new MainWindow(this) { Icon = Resources.DeceiveIcon };
        _window.ShowAndFocus(activate);
    });

    public async Task SetStatusAsync(string status)
    {
        Status = status;
        await UpdateStatusAsync(status);
        Enabled = true;
        SaveStatus();
        UpdateTray();
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        if (Enabled == enabled)
            return;
        Enabled = enabled;
        await UpdateStatusAsync(Enabled ? Status : "chat");
        await SendMessageFromFakePlayerAsync(Enabled ? $"{AppInfo.Name} is now enabled." : $"{AppInfo.Name} is now disabled.");
        UpdateTray();
    }

    /// <summary>Applies settings changed from the app window, without chat messages.</summary>
    public async Task ApplySettingsQuietlyAsync()
    {
        try
        {
            foreach (var connection in Connections.ToList())
                await connection.ApplySettingsAsync();
            UpdateTray();
        }
        catch (Exception e)
        {
            Trace.WriteLine("Error applying settings: " + e);
        }
    }

    public bool IsConnected => Connections.Count > 0;

    private NotifyIcon TrayIcon { get; }
    public bool Enabled { get; set; } = true;
    public string Status { get; set; } = null!;
    private string StatusFile { get; } = Path.Combine(Persistence.DataDir, "status");
    public bool ConnectToMuc { get; set; } = true;
    private bool SentIntroductionText { get; set; } = false;
    private CancellationTokenSource? ShutdownToken { get; set; } = null;

    private ToolStripMenuItem EnabledMenuItem { get; set; } = null!;
    private ToolStripMenuItem ChatStatus { get; set; } = null!;
    private ToolStripMenuItem OfflineStatus { get; set; } = null!;
    private ToolStripMenuItem MobileStatus { get; set; } = null!;

    private List<ProxiedConnection> Connections { get; } = new();

    public void StartServingClients(TcpListener server, X509Certificate2 serverCert, string chatHost, int chatPort)
    {
        Task.Run(() => ServeClientsAsync(server, serverCert, chatHost, chatPort));
    }

    private async Task ServeClientsAsync(TcpListener server, X509Certificate2 serverCert, string chatHost, int chatPort)
    {
        while (true)
        {
            try
            {
                // no need to shutdown, we received a new request
                ShutdownToken?.Cancel();
                ShutdownToken = null;

                var incoming = await server.AcceptTcpClientAsync();
                var sslIncoming = new SslStream(incoming.GetStream());
                await sslIncoming.AuthenticateAsServerAsync(serverCert);

                TcpClient outgoing;
                while (true)
                {
                    try
                    {
                        outgoing = new TcpClient(chatHost, chatPort);
                        break;
                    }
                    catch (SocketException e)
                    {
                        Trace.WriteLine(e);
                        var result = MessageBox.Show(
                            "Unable to connect to the chat server. Please check your internet connection and try again.",
                            StartupHandler.DeceiveTitle,
                            MessageBoxButtons.RetryCancel,
                            MessageBoxIcon.Error,
                            MessageBoxDefaultButton.Button1
                        );
                        if (result == DialogResult.Cancel)
                            Environment.Exit(0);
                    }
                }

                var sslOutgoing = new SslStream(outgoing.GetStream());
                await sslOutgoing.AuthenticateAsClientAsync(chatHost);

                var proxiedConnection = new ProxiedConnection(this, sslIncoming, sslOutgoing);
                proxiedConnection.Start();
                proxiedConnection.ConnectionErrored += (_, _) =>
                {
                    Trace.WriteLine("Disconnected incoming connection.");
                    Connections.Remove(proxiedConnection);

                    if (Connections.Count == 0)
                    {
                        Task.Run(ShutdownIfNoReconnect);
                    }
                };
                Connections.Add(proxiedConnection);

                if (!SentIntroductionText && !Persistence.GetHasShownIntroduction())
                {
                    SentIntroductionText = true;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(10_000);
                        await SendIntroductionTextAsync();
                    });
                }
            } catch (Exception e)
            {
                Trace.WriteLine("Failed to handle incoming connection.");
                Trace.WriteLine(e);
            }
        }
    }

    private void UpdateTray()
    {
        if (_ui.InvokeRequired)
        {
            RunOnUi(UpdateTray);
            return;
        }

        var openWindowItem = new ToolStripMenuItem($"Open {AppInfo.Name}", null, (_, _) => ShowWindow())
        {
            Font = new System.Drawing.Font(SystemFonts.MenuFont, System.Drawing.FontStyle.Bold)
        };

        var aboutMenuItem = new ToolStripMenuItem(StartupHandler.DeceiveTitle) { Enabled = false };

        EnabledMenuItem = new ToolStripMenuItem("Enabled", null, async (_, _) => await SetEnabledAsync(!Enabled))
        { Checked = Enabled };

        var mucMenuItem = new ToolStripMenuItem("Enable lobby chat", null, (_, _) =>
        {
            ConnectToMuc = !ConnectToMuc;
            UpdateTray();
        })
        { Checked = ConnectToMuc };

        ChatStatus = new ToolStripMenuItem("Online", null, async (_, _) => await SetStatusAsync("chat"))
        { Checked = Status.Equals("chat") };

        OfflineStatus = new ToolStripMenuItem("Offline", null, async (_, _) => await SetStatusAsync("offline"))
        { Checked = Status.Equals("offline") };

        MobileStatus = new ToolStripMenuItem("Mobile", null, async (_, _) => await SetStatusAsync("mobile"))
        { Checked = Status.Equals("mobile") };

        var typeMenuItem = new ToolStripMenuItem("Status Type", null, ChatStatus, OfflineStatus, MobileStatus);
        
        var currentStartup = Persistence.GetStartupStatus();
        var startupOnline = new ToolStripMenuItem("Online", null, (_, _) =>
        {
            Persistence.SetStartupStatus("chat");
            UpdateTray();
        })
        { Checked = currentStartup == "chat" };

        var startupOffline = new ToolStripMenuItem("Offline", null, (_, _) =>
        {
            Persistence.SetStartupStatus("offline");
            UpdateTray();
        })
        { Checked = currentStartup == "offline" };

        var startupMobile = new ToolStripMenuItem("Mobile", null, (_, _) =>
        {
            Persistence.SetStartupStatus("mobile");
            UpdateTray();
        })
        { Checked = currentStartup == "mobile" };

        var startupLast = new ToolStripMenuItem("Remember Last", null, (_, _) =>
        {
            Persistence.SetStartupStatus("last");
            UpdateTray();
        })
        { Checked = currentStartup == "last" };

        var startupStatusMenuItem = new ToolStripMenuItem("Default Status on Startup", null, startupOnline, startupOffline, startupMobile, startupLast);

        var openSettingsItem = new ToolStripMenuItem("Open settings file", null, (_, _) => AppSettings.OpenInEditor());
        var reloadSettingsItem = new ToolStripMenuItem("Reload settings", null, async (_, _) =>
        {
            AppSettings.Load();
            await OnSettingsReloadedAsync();
        });
        var visibleCount = AppSettings.Current.VisibleTo.Count;
        var visibleInfoItem = new ToolStripMenuItem(
            visibleCount == 0 ? "Visible to: nobody" : $"Visible to: {visibleCount} friend{(visibleCount == 1 ? "" : "s")}") { Enabled = false };
        var settingsMenuItem = new ToolStripMenuItem("Settings", null, openSettingsItem, reloadSettingsItem, new ToolStripSeparator(), visibleInfoItem);

        var restartWithDifferentGameItem = new ToolStripMenuItem("Restart and launch a different game", null, (_, _) =>
        {
            var result = MessageBox.Show(
                $"Restart {AppInfo.Name} to launch a different game? This will also stop related games if they are running.",
                StartupHandler.DeceiveTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1
            );

            if (result is not DialogResult.Yes)
                return;

            Utils.KillProcesses();
            Thread.Sleep(2000);

            Persistence.SetDefaultLaunchGame(LaunchGame.Prompt);
            Process.Start(Application.ExecutablePath);
            Environment.Exit(0);
        });

        var quitMenuItem = new ToolStripMenuItem("Quit", null, (_, _) =>
        {
            var result = MessageBox.Show(
                $"Are you sure you want to stop {AppInfo.Name}? This will also stop related games if they are running.",
                StartupHandler.DeceiveTitle,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1
            );

            if (result is not DialogResult.Yes)
                return;

            Utils.KillProcesses();
            SaveStatus();
            _window?.Dispose();
            TrayIcon.Visible = false;
            Application.Exit();
        });

        TrayIcon.ContextMenuStrip = new ContextMenuStrip();

#if DEBUG
        var sendTestMsg = new ToolStripMenuItem("Send message", null, async (_, _) => { await SendMessageFromFakePlayerAsync("Test"); });

        TrayIcon.ContextMenuStrip.Items.AddRange(new ToolStripItem[]
        {
            openWindowItem, new ToolStripSeparator(), aboutMenuItem, EnabledMenuItem, typeMenuItem, startupStatusMenuItem, mucMenuItem, settingsMenuItem, sendTestMsg, restartWithDifferentGameItem, quitMenuItem
        });
#else
        TrayIcon.ContextMenuStrip.Items.AddRange(new ToolStripItem[] { openWindowItem, new ToolStripSeparator(), aboutMenuItem, EnabledMenuItem, typeMenuItem, startupStatusMenuItem, mucMenuItem, settingsMenuItem, restartWithDifferentGameItem, quitMenuItem });
#endif

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task HandleChatMessage(string content)
    {
        var body = System.Text.RegularExpressions.Regex.Match(content, "<body>(.*?)</body>", System.Text.RegularExpressions.RegexOptions.Singleline);
        var text = body.Success ? body.Groups[1].Value.Trim().ToLower() : string.Empty;

        if (text is "visible" or "who" or "list")
        {
            await SendVisibleToSummaryAsync();
        }
        else if (text is "settings" or "reload")
        {
            AppSettings.Load();
            await OnSettingsReloadedAsync();
        }
        else if (content.ToLower().Contains("offline"))
        {
            if (!Enabled)
                await SendMessageFromFakePlayerAsync($"{AppInfo.Name} is now enabled.");
            await SetStatusAsync("offline");
        }
        else if (content.ToLower().Contains("mobile"))
        {
            if (!Enabled)
                await SendMessageFromFakePlayerAsync($"{AppInfo.Name} is now enabled.");
            await SetStatusAsync("mobile");
        }
        else if (content.ToLower().Contains("online"))
        {
            if (!Enabled)
                await SendMessageFromFakePlayerAsync($"{AppInfo.Name} is now enabled.");
            await SetStatusAsync("chat");
        }
        else if (content.ToLower().Contains("enable"))
        {
            if (Enabled)
                await SendMessageFromFakePlayerAsync($"{AppInfo.Name} is already enabled.");
            else
                await SetEnabledAsync(!Enabled);
        }
        else if (content.ToLower().Contains("disable"))
        {
            if (!Enabled)
                await SendMessageFromFakePlayerAsync($"{AppInfo.Name} is already disabled.");
            else
                await SetEnabledAsync(!Enabled);
        }
        else if (content.ToLower().Contains("status"))
        {
            if (Status == "chat")
                await SendMessageFromFakePlayerAsync("You are appearing online.");
            else
            {
                await SendMessageFromFakePlayerAsync("You are appearing " + Status + ".");
                await SendVisibleToSummaryAsync();
            }
        }
        else if (content.ToLower().Contains("help"))
        {
            await SendMessageFromFakePlayerAsync($"You can send the following messages to quickly change {AppInfo.Name} settings: online/offline/mobile/enable/disable/status/visible/reload. " +
                                                 "To pick friends who still see you online, use Settings > Open settings file in the tray menu.");
        }
    }

    private async Task SendIntroductionTextAsync()
    {
        Persistence.SetHasShownIntroduction();
        await SendMessageFromFakePlayerAsync($"Welcome! {AppInfo.Name} is running and you are currently appearing " + Status +
                                             $". Despite what the game client may indicate, you are appearing offline to your friends unless you manually disable {AppInfo.Name}.");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync(
            $"If you want to invite others while being offline, you may need to disable {AppInfo.Name} for them to accept. You can enable {AppInfo.Name} again as soon as they are in your lobby.");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync($"To enable or disable {AppInfo.Name}, or to configure other settings, find {AppInfo.Name} in your tray icons.");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync("Want some friends to still see you online? Add their Riot IDs to \"visibleTo\" in the settings file (tray icon > Settings > Open settings file).");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync("Have fun!");
    }

    private async Task OnSettingsReloadedAsync()
    {
        try
        {
            foreach (var connection in Connections.ToList())
                await connection.ApplySettingsAsync();

            UpdateTray();

            if (AppSettings.LastError is not null)
            {
                await SendMessageFromFakePlayerAsync("Couldn't read the settings file, so the old settings are still in use: " + AppSettings.LastError);
                return;
            }

            await SendMessageFromFakePlayerAsync("Settings reloaded.");
            await SendVisibleToSummaryAsync();
        }
        catch (Exception e)
        {
            Trace.WriteLine("Error applying settings: " + e);
        }
    }

    private async Task SendVisibleToSummaryAsync()
    {
        var entries = AppSettings.Current.VisibleTo;
        if (entries.Count == 0)
        {
            await SendMessageFromFakePlayerAsync("Nobody is on your visible list, so everyone sees you as " + (Status == "chat" ? "online" : Status) + ".");
            return;
        }

        FriendDirectory.Resolve(entries, out var matched, out var notFound);

        if (matched.Count > 0)
            await SendMessageFromFakePlayerAsync(
                (Status == "chat" || !Enabled ? "When you appear offline/mobile, these friends will still see you online: " : "These friends can still see you online: ") +
                string.Join(", ", matched));

        if (notFound.Count > 0)
            await SendMessageFromFakePlayerAsync(
                (FriendDirectory.Count == 0
                    ? "Your friends list hasn't loaded yet, so these couldn't be matched: "
                    : "Couldn't find these in your friends list (check the Riot ID and #tag): ") +
                string.Join(", ", notFound));
    }

    private async Task SendMessageFromFakePlayerAsync(string message)
    {
        message = SecurityElement.Escape(message);
        foreach (var connection in Connections)
            await connection.SendMessageFromFakePlayerAsync(message);
    }

    private async Task UpdateStatusAsync(string newStatus)
    {
        foreach (var connection in Connections)
            await connection.UpdateStatusAsync(newStatus);

        if (newStatus == "chat")
            await SendMessageFromFakePlayerAsync("You are now appearing online.");
        else
            await SendMessageFromFakePlayerAsync("You are now appearing " + newStatus + ".");
    }

    private void LoadStatus()
    {
        var startupStatus = Persistence.GetStartupStatus();

        if (startupStatus is "chat" or "offline" or "mobile")
        {
            Status = startupStatus;
            return;
        }

        if (!File.Exists(StatusFile))
        {
            Status = "offline";
            return;
        }

        // "last" or unrecognized: use the saved session status.
        var saved = File.ReadAllText(StatusFile);
        Status = saved switch
        {
            "chat" => "chat",
            "mobile" => "mobile",
            _ => "offline"
        };
    }

    private async Task ShutdownIfNoReconnect()
    {
        if (ShutdownToken == null)
            ShutdownToken = new CancellationTokenSource();
        await Task.Delay(60_000, ShutdownToken.Token);

        Trace.WriteLine("Received no new connections after 60s, shutting down.");
        Environment.Exit(0);
    }

    private void SaveStatus() => File.WriteAllText(StatusFile, Status);
}
