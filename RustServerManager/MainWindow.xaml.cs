using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using RustServerManager.Services;

namespace RustServerManager
{
    public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
    {
        private Process? _serverProcess;
        private RconService _rcon;
        private ObservableCollection<Player> _players;
        private ObservableCollection<UmodPlugin> _umodPlugins;
        private ObservableCollection<InstalledPlugin> _installedPlugins;

        private string _serverRoot = "";
        private string _cfgPath = "";
        private string _currentConfigPath = "";
        private string _settingsPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

        private DispatcherTimer _statsTimer;
        private DateTime _lastCpuTime = DateTime.Now;
        private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;

        private bool _isWinterThemeActive = false;

        public class AppSettings
        {
            public string ServerPath { get; set; } = "";
            public bool WinterThemeEnabled { get; set; } = false;
        }

        public MainWindow()
        {
            InitializeComponent();
            _players = new ObservableCollection<Player>();
            PlayersGrid.ItemsSource = _players;

            _umodPlugins = new ObservableCollection<UmodPlugin>();
            PluginsGrid.ItemsSource = _umodPlugins;

            _installedPlugins = new ObservableCollection<InstalledPlugin>();
            InstalledPluginsList.ItemsSource = _installedPlugins;

            _rcon = new RconService();
            _rcon.OnLogMessage += (msg) => Dispatcher.Invoke(() =>
            {
                RconLog.AppendText(msg + "\n");
                RconLog.ScrollToEnd();
            });

            _rcon.OnPlayerListReceived += (msg) => Dispatcher.Invoke(() => ParsePlayerList(msg));

            _statsTimer = new DispatcherTimer();
            _statsTimer.Interval = TimeSpan.FromSeconds(1);
            _statsTimer.Tick += StatsTimer_Tick;

            LoadSettings();
        }

        private void UpdateStatus(string text, string colorHex)
        {
            Dispatcher.Invoke(() =>
            {
                if (StatusText != null) StatusText.Text = text;
                if (StatusDot != null)
                {
                    var colorObj = ColorConverter.ConvertFromString(colorHex);
                    if (colorObj is Color color) StatusDot.Fill = new SolidColorBrush(color);
                }
            });
        }

        private void LoadSettings()
        {
            bool isWinter = false;
            if (File.Exists(_settingsPath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (settings != null)
                    {
                        if (!string.IsNullOrEmpty(settings.ServerPath)) _serverRoot = settings.ServerPath;
                        isWinter = settings.WinterThemeEnabled;
                    }
                }
                catch { }
            }

            TxtServerPath.Text = _serverRoot;
            RefreshPaths();

            if (!string.IsNullOrEmpty(_serverRoot) && File.Exists(_cfgPath))
            {
                LoadCfgSettings();
            }
            else
            {
                TxtServerName.Text = "Мой сервер Rust";
                TxtServerSeed.Text = "1337";
            }

            if (WinterThemeToggle != null) WinterThemeToggle.IsChecked = isWinter;
            ToggleWinterTheme(isWinter);
        }

        private void BrowseServer_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Выберите папку, где лежит RustDedicated.exe" };
            if (dialog.ShowDialog() == true)
            {
                _serverRoot = dialog.FolderName;
                TxtServerPath.Text = _serverRoot;
                var settings = new AppSettings { ServerPath = _serverRoot, WinterThemeEnabled = _isWinterThemeActive };
                File.WriteAllText(_settingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
                RefreshPaths();
                if (File.Exists(_cfgPath)) LoadCfgSettings();
                RefreshInstalledPluginsList();
            }
        }

        private void RefreshPaths()
        {
            if (string.IsNullOrEmpty(_serverRoot)) { _cfgPath = ""; return; }
            _cfgPath = System.IO.Path.Combine(_serverRoot, "server", "vibe_server", "cfg", "server.cfg");
        }

        // --- УТИЛИТЫ (БЭКАПЫ И ВАЙП) ---

        private async void CreateBackup_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serverRoot)) { MessageBox.Show("Выберите папку с сервером!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var btn = sender as Button; if (btn != null) btn.IsEnabled = false;

            try
            {
                string backupDir = System.IO.Path.Combine(_serverRoot, "backups");
                if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

                string zipName = $"backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip";
                string zipPath = System.IO.Path.Combine(backupDir, zipName);

                string serverDataDir = System.IO.Path.Combine(_serverRoot, "server", "vibe_server");
                string oxideDir = System.IO.Path.Combine(_serverRoot, "oxide");

                await Task.Run(() =>
                {
                    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                    {
                        // Добавляем папку server/vibe_server
                        if (Directory.Exists(serverDataDir))
                        {
                            foreach (var file in Directory.GetFiles(serverDataDir, "*", SearchOption.AllDirectories))
                            {
                                var entry = zip.CreateEntry("server/" + System.IO.Path.GetRelativePath(serverDataDir, file));
                                using var stream = entry.Open();
                                using var fileStream = File.OpenRead(file);
                                fileStream.CopyTo(stream);
                            }
                        }
                        // Добавляем папку oxide
                        if (Directory.Exists(oxideDir))
                        {
                            foreach (var file in Directory.GetFiles(oxideDir, "*", SearchOption.AllDirectories))
                            {
                                var entry = zip.CreateEntry("oxide/" + System.IO.Path.GetRelativePath(oxideDir, file));
                                using var stream = entry.Open();
                                using var fileStream = File.OpenRead(file);
                                fileStream.CopyTo(stream);
                            }
                        }
                    }
                });

                MessageBox.Show($"Бэкап успешно создан!\nФайл: {zipName}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show($"Ошибка бэкапа: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { if (btn != null) btn.IsEnabled = true; }
        }

        private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serverRoot)) return;
            string backupDir = System.IO.Path.Combine(_serverRoot, "backups");
            try { if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir); Process.Start("explorer.exe", backupDir); } catch { }
        }

        private void WipeServer_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serverRoot)) return;
            if (_serverProcess != null && !_serverProcess.HasExited) { MessageBox.Show("Остановите сервер перед вайпом!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var result = MessageBox.Show("Удалить карту и сохранения (.map, .sav)? Плагины останутся.", "Подтверждение вайпа", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No) return;

            try
            {
                string serverDataDir = System.IO.Path.Combine(_serverRoot, "server", "vibe_server");
                if (Directory.Exists(serverDataDir))
                {
                    var filesToDelete = Directory.GetFiles(serverDataDir, "*.sav").Concat(Directory.GetFiles(serverDataDir, "*.map")).ToList();
                    foreach (var file in filesToDelete) File.Delete(file);
                    MessageBox.Show($"Вайп завершен! Удалено файлов: {filesToDelete.Count}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex) { MessageBox.Show($"Ошибка вайпа: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void WipeBlueprints_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serverRoot)) return;
            if (_serverProcess != null && !_serverProcess.HasExited) { MessageBox.Show("Остановите сервер перед вайпом!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var result = MessageBox.Show("Удалить чертежи игроков (player.blueprints)?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No) return;

            try
            {
                string bpFile = System.IO.Path.Combine(_serverRoot, "server", "vibe_server", "player.blueprints.0.0.0.0.sav");
                if (!File.Exists(bpFile)) bpFile = System.IO.Path.Combine(_serverRoot, "server", "vibe_server", "player.blueprints");

                if (File.Exists(bpFile)) { File.Delete(bpFile); MessageBox.Show("Чертежи удалены!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information); }
                else MessageBox.Show("Файл чертежей не найден.", "Инфо", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void QuickSave_Click(object sender, RoutedEventArgs e) => _rcon.SendCommand("server.save");
        private void QuickReloadOxide_Click(object sender, RoutedEventArgs e) => _rcon.SendCommand("oxide.reload *");

        // ОБНОВЛЕННАЯ КНОПКА НАПИСАТЬ В ЧАТ
        private void QuickBroadcast_Click(object sender, RoutedEventArgs e)
        {
            // Создаем кастомное окно ввода
            var inputWindow = new Window
            {
                Title = "Объявление в чат",
                Width = 450,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = (Brush)new BrushConverter().ConvertFromString("#1E1E1E")!,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow
            };

            var grid = new Grid { Margin = new Thickness(20) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock { Text = "Введите текст для отправки в чат:", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(label, 0);
            grid.Children.Add(label);

            var textBox = new TextBox { Background = Brushes.Black, Foreground = Brushes.White, Padding = new Thickness(5), VerticalContentAlignment = VerticalAlignment.Center, AcceptsReturn = false };
            Grid.SetRow(textBox, 1);
            grid.Children.Add(textBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };

            var sendBtn = new Button { Content = "Отправить", Width = 100, Margin = new Thickness(0, 0, 10, 0), Background = (Brush)new BrushConverter().ConvertFromString("#17a2b8")!, Foreground = Brushes.Black, FontWeight = FontWeights.Bold };
            sendBtn.Click += (s, ev) =>
            {
                string text = textBox.Text.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    // Отправляем команду say с кавычками, чтобы чат не ломался от пробелов
                    _rcon.SendCommand($"say \"{text}\"");
                    inputWindow.Close();
                }
            };

            var cancelBtn = new Button { Content = "Отмена", Width = 100, Background = Brushes.Gray, Foreground = Brushes.Black };
            cancelBtn.Click += (s, ev) => inputWindow.Close();

            btnPanel.Children.Add(sendBtn);
            btnPanel.Children.Add(cancelBtn);
            Grid.SetRow(btnPanel, 2);
            grid.Children.Add(btnPanel);

            inputWindow.Content = grid;

            // Фокусируемся на поле ввода сразу при открытии
            textBox.Focus();

            inputWindow.ShowDialog();
        }

        // --- ЗИМА ---

        private void WinterTheme_Changed(object sender, RoutedEventArgs e)
        {
            _isWinterThemeActive = WinterThemeToggle.IsChecked == true;
            ToggleWinterTheme(_isWinterThemeActive);

            var settings = new AppSettings { ServerPath = _serverRoot, WinterThemeEnabled = _isWinterThemeActive };
            File.WriteAllText(_settingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }

        private void ToggleWinterTheme(bool enable)
        {
            if (SnowCanvas == null) return;
            if (enable) { SnowCanvas.Visibility = Visibility.Visible; GenerateSnowflakes(); }
            else { SnowCanvas.Visibility = Visibility.Collapsed; SnowCanvas.Children.Clear(); }
        }

        private void GenerateSnowflakes()
        {
            if (SnowCanvas == null) return;
            SnowCanvas.Children.Clear();
            int count = 60;
            var random = new Random();
            double width = SystemParameters.PrimaryScreenWidth;

            for (int i = 0; i < count; i++)
            {
                var snowflake = new TextBlock { Text = "❄", Foreground = Brushes.White, FontSize = random.Next(10, 25), Opacity = random.NextDouble() * 0.6 + 0.2 };
                double startX = random.NextDouble() * width;
                double startY = random.NextDouble() * 600 - 600;

                Canvas.SetLeft(snowflake, startX); Canvas.SetTop(snowflake, startY);

                var fallAnimation = new DoubleAnimation { From = startY, To = 800, Duration = TimeSpan.FromSeconds(random.Next(6, 15)), RepeatBehavior = RepeatBehavior.Forever };
                var driftAnimation = new DoubleAnimation { From = startX, To = startX + random.Next(-40, 40), Duration = TimeSpan.FromSeconds(random.Next(3, 6)), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };

                snowflake.BeginAnimation(Canvas.TopProperty, fallAnimation); snowflake.BeginAnimation(Canvas.LeftProperty, driftAnimation);
                SnowCanvas.Children.Add(snowflake);
            }
        }

        // --- ОСТАЛЬНАЯ ЛОГИКА ---

        private async void UpdateServer_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serverRoot)) { MessageBox.Show("Выберите папку с сервером!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (_serverProcess != null && !_serverProcess.HasExited) { MessageBox.Show("Остановите сервер!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            KillZombieProcesses();
            var btn = sender as Button; if (btn != null) btn.IsEnabled = false;
            ConsoleOutput.AppendText("=== НАЧАЛО ОБНОВЛЕНИЯ ===\n");

            string steamCmdDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamCMD");
            string steamCmdExe = System.IO.Path.Combine(steamCmdDir, "steamcmd.exe");

            try
            {
                if (!File.Exists(steamCmdExe))
                {
                    ConsoleOutput.AppendText("Скачиваем SteamCMD...\n");
                    if (!Directory.Exists(steamCmdDir)) Directory.CreateDirectory(steamCmdDir);
                    using var client = new HttpClient();
                    byte[] fileData = await client.GetByteArrayAsync("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");
                    string zipPath = System.IO.Path.Combine(steamCmdDir, "steamcmd.zip");
                    await File.WriteAllBytesAsync(zipPath, fileData);
                    ZipFile.ExtractToDirectory(zipPath, steamCmdDir);
                    File.Delete(zipPath);
                }

                ConsoleOutput.AppendText("Запуск SteamCMD...\n");
                var startInfo = new ProcessStartInfo
                {
                    FileName = steamCmdExe,
                    Arguments = $"+force_install_dir \"{_serverRoot}\" +login anonymous +app_update 258550 -beta public validate +quit",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var process = new Process { StartInfo = startInfo };
                process.OutputDataReceived += (s, ev) => { if (ev.Data != null) Dispatcher.Invoke(() => { ConsoleOutput.AppendText(ev.Data + "\n"); ConsoleOutput.ScrollToEnd(); }); };
                process.ErrorDataReceived += (s, ev) => { if (ev.Data != null) Dispatcher.Invoke(() => { ConsoleOutput.AppendText("[ОШИБКА] " + ev.Data + "\n"); ConsoleOutput.ScrollToEnd(); }); };

                process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                await process.WaitForExitAsync();
                ConsoleOutput.AppendText("=== ОБНОВЛЕНИЕ ЗАВЕРШЕНО ===\n");
            }
            catch (Exception ex) { ConsoleOutput.AppendText($"[ОШИБКА] {ex.Message}\n"); }
            finally { if (btn != null) btn.IsEnabled = true; }
        }

        private void KillZombieProcesses()
        {
            try
            {
                var zombies = Process.GetProcessesByName("RustDedicated");
                if (zombies.Length > 0)
                {
                    ConsoleOutput.AppendText($"[Система] Убиваю {zombies.Length} зависших процессов...\n");
                    foreach (var z in zombies) { try { z.Kill(); z.WaitForExit(1000); } catch { } }
                    ConsoleOutput.AppendText("[Система] Порт 28016 свободен.\n");
                    System.Threading.Thread.Sleep(500);
                }
            }
            catch { }
        }

        private void StatsTimer_Tick(object? sender, EventArgs e)
        {
            var process = _serverProcess;
            if (process == null || process.HasExited) { CpuStat.Text = "0%"; RamStat.Text = "0 MB"; UptimeStat.Text = "00:00:00"; return; }

            try
            {
                long ramMb = process.WorkingSet64 / 1024 / 1024;
                RamStat.Text = $"{ramMb} MB";
                TimeSpan uptime = DateTime.Now - process.StartTime;
                UptimeStat.Text = $"{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";

                var currentTotalProcessorTime = process.TotalProcessorTime;
                var currentTime = DateTime.Now;
                var cpuUsedMs = (currentTotalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds;
                var totalMsPassed = (currentTime - _lastCpuTime).TotalMilliseconds;
                var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
                double cpuPercent = Math.Min(cpuUsageTotal * 100, 100);
                CpuStat.Text = $"{cpuPercent:0}%";

                _lastTotalProcessorTime = currentTotalProcessorTime;
                _lastCpuTime = currentTime;
                PlayersStat.Text = _players.Count.ToString();
            }
            catch { }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            try
            {
                _statsTimer?.Stop();
                _rcon.Disconnect();
                var process = _serverProcess;
                if (process != null && !process.HasExited) { process.Kill(); process.WaitForExit(1000); }
            }
            catch { }
        }

        private void StartServer_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serverRoot)) { MessageBox.Show("Выберите папку с сервером!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (_serverProcess != null && !_serverProcess.HasExited) return;

            KillZombieProcesses();
            SaveSettings_Click(null, null);

            string exePath = System.IO.Path.Combine(_serverRoot, "RustDedicated.exe");
            if (!File.Exists(exePath)) { MessageBox.Show($"Не найден RustDedicated.exe!", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); return; }

            string safePassword = TxtRconPassword.Text.Trim();
            if (safePassword.Length < 8) safePassword = "SuperSecret123!";

            string args = $"-batchmode -disable-server-occlusion +server.identity vibe_server +server.hostname \"{TxtServerName.Text}\" +server.maxplayers {TxtMaxPlayers.Text} +server.worldsize {TxtWorldSize.Text} +server.seed {TxtServerSeed.Text} +rcon.web 1 +rcon.port 28016 +rcon.password {safePassword}";

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = _serverRoot
            };

            var newProcess = new Process { StartInfo = startInfo };
            newProcess.OutputDataReceived += (s, ev) =>
            {
                if (ev.Data != null) Dispatcher.Invoke(() =>
                {
                    ConsoleOutput.AppendText(ev.Data + "\n");
                    ConsoleOutput.ScrollToEnd();
                    if (ev.Data.Contains("Server startup complete")) UpdateStatus("Сервер запущен", "#28a745");
                });
            };
            newProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) Dispatcher.Invoke(() => { ConsoleOutput.AppendText("[ОШИБКА] " + ev.Data + "\n"); ConsoleOutput.ScrollToEnd(); }); };

            newProcess.Start();
            newProcess.BeginOutputReadLine();
            newProcess.BeginErrorReadLine();
            newProcess.EnableRaisingEvents = true;
            newProcess.Exited += (s, ev) => { UpdateStatus("Сервер выключен", "#dc3545"); };

            _serverProcess = newProcess;
            ConsoleOutput.AppendText("Сервер запускается...\n");
            UpdateStatus("Сервер запускается...", "#ffc107");

            _lastCpuTime = DateTime.Now;
            _lastTotalProcessorTime = newProcess.TotalProcessorTime;
            _statsTimer.Start();
        }

        private void StopServer_Click(object sender, RoutedEventArgs e)
        {
            var process = _serverProcess;
            if (process != null && !process.HasExited)
            {
                process.Kill();
                ConsoleOutput.AppendText("Сервер остановлен.\n");
                _serverProcess = null;
                _statsTimer.Stop();
                CpuStat.Text = "0%"; RamStat.Text = "0 MB"; UptimeStat.Text = "00:00:00"; PlayersStat.Text = "0";
                UpdateStatus("Сервер выключен", "#dc3545");
            }
        }

        private void ParsePlayerList(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            _players.Clear();
            try
            {
                var list = JsonConvert.DeserializeObject<List<Player>>(data);
                if (list != null) { foreach (var p in list) _players.Add(p); }
            }
            catch
            {
                var matches = Regex.Matches(data, @"(?<id>\d{17})\s+""?(?<name>[^""]+)""?\s+(?<ping>\d+)");
                foreach (Match match in matches) _players.Add(new Player { SteamId = match.Groups["id"].Value, Name = match.Groups["name"].Value.Trim(), Ping = match.Groups["ping"].Value });
            }
            PlayersStat.Text = _players.Count.ToString();
        }

        private void RefreshInstalledPlugins_Click(object sender, RoutedEventArgs e) => RefreshInstalledPluginsList();

        private void RefreshInstalledPluginsList()
        {
            _installedPlugins.Clear();
            if (string.IsNullOrEmpty(_serverRoot)) return;
            string pluginsDir = System.IO.Path.Combine(_serverRoot, "oxide", "plugins");
            if (!Directory.Exists(pluginsDir)) { Directory.CreateDirectory(pluginsDir); return; }

            var files = Directory.GetFiles(pluginsDir, "*.cs");
            foreach (var file in files) _installedPlugins.Add(new InstalledPlugin { Name = System.IO.Path.GetFileNameWithoutExtension(file) });
        }

        private void InstalledPluginsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (InstalledPluginsList.SelectedItem is not InstalledPlugin selectedPlugin || string.IsNullOrEmpty(_serverRoot)) return;

            string configPath = System.IO.Path.Combine(_serverRoot, "oxide", "config", $"{selectedPlugin.Name}.json");
            _currentConfigPath = configPath;

            if (File.Exists(configPath))
            {
                try { ConfigHeader.Text = $"Конфиг плагина: {selectedPlugin.Name}.json"; ConfigEditor.Text = File.ReadAllText(configPath); SaveConfigBtn.IsEnabled = true; }
                catch (Exception ex) { ConfigHeader.Text = "Ошибка чтения"; ConfigEditor.Text = ex.Message; SaveConfigBtn.IsEnabled = false; }
            }
            else { ConfigHeader.Text = $"Конфиг не найден"; ConfigEditor.Text = ""; SaveConfigBtn.IsEnabled = false; }
        }

        private void SavePluginConfig_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentConfigPath) || !SaveConfigBtn.IsEnabled) return;
            try { File.WriteAllText(_currentConfigPath, ConfigEditor.Text); MessageBox.Show("Сохранено!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information); }
            catch (Exception ex) { MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void SearchPlugins_Click(object sender, RoutedEventArgs e)
        {
            string query = TxtPluginSearch.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;
            var btn = sender as Button; if (btn != null) btn.IsEnabled = false;
            PluginLog.AppendText($"Поиск: \"{query}\"...\n"); PluginLog.ScrollToEnd(); _umodPlugins.Clear();

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "RustServerManager-App");
                string searchUrl = $"https://umod.org/plugins/search.json?query={Uri.EscapeDataString(query)}&page=1&sort=latest_release_at&sortdir=desc&filter=&categories%5B%5D=rust";
                string jsonResponse = await client.GetStringAsync(searchUrl);

                dynamic? result = JsonConvert.DeserializeObject(jsonResponse);
                if (result?.data == null) return;

                int foundCount = 0;
                foreach (var item in result.data)
                {
                    string name = item.name?.ToString() ?? "Неизвестно";
                    string author = item.author?.ToString() ?? "Неизвестно";
                    string version = item.version?.ToString() ?? "—";
                    string slug = item.slug?.ToString() ?? item.file_name?.ToString() ?? "";
                    if (string.IsNullOrEmpty(slug)) slug = name.ToLower().Replace(" ", "-").Replace("'", "");

                    _umodPlugins.Add(new UmodPlugin { Name = name, Author = author, Version = version, Slug = slug });
                    foundCount++;
                }
                PluginLog.AppendText($"Найдено: {foundCount}\n"); PluginLog.ScrollToEnd();
            }
            catch (Exception ex) { PluginLog.AppendText($"[ОШИБКА] {ex.Message}\n"); PluginLog.ScrollToEnd(); }
            finally { if (btn != null) btn.IsEnabled = true; }
        }

        private void ClearPluginSearch_Click(object sender, RoutedEventArgs e) { TxtPluginSearch.Text = string.Empty; _umodPlugins.Clear(); }

        private void TxtPluginSearch_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { SearchPlugins_Click(sender, e); e.Handled = true; } }

        private async void InstallPlugin_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serverRoot)) return;
            var btn = sender as Button;
            if (btn?.DataContext is not UmodPlugin plugin || string.IsNullOrEmpty(plugin.Slug)) return;

            btn.IsEnabled = false; PluginLog.AppendText($"Установка \"{plugin.Name}\"...\n"); PluginLog.ScrollToEnd();
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "RustServerManager-App");
                byte[] fileData = await client.GetByteArrayAsync($"https://umod.org/plugins/{plugin.Slug}.cs");

                string pluginsDir = System.IO.Path.Combine(_serverRoot, "oxide", "plugins");
                if (!Directory.Exists(pluginsDir)) Directory.CreateDirectory(pluginsDir);
                await File.WriteAllBytesAsync(System.IO.Path.Combine(pluginsDir, $"{plugin.Slug}.cs"), fileData);

                PluginLog.AppendText($"[УСПЕХ]\n"); PluginLog.ScrollToEnd();
                RefreshInstalledPluginsList();
            }
            catch (Exception ex) { PluginLog.AppendText($"[ОШИБКА] {ex.Message}\n"); PluginLog.ScrollToEnd(); }
            finally { btn.IsEnabled = true; }
        }

        private void OpenPluginsFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serverRoot)) return;
            string pluginsPath = System.IO.Path.Combine(_serverRoot, "oxide", "plugins");
            try { if (!Directory.Exists(pluginsPath)) Directory.CreateDirectory(pluginsPath); Process.Start("explorer.exe", pluginsPath); } catch { }
        }

        private async void DownloadOxide_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_serverRoot) || (_serverProcess != null && !_serverProcess.HasExited)) return;
            var btn = sender as Button; if (btn != null) btn.IsEnabled = false;
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "RustServerManager-App");
                byte[] fileData = await client.GetByteArrayAsync("https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust.zip");
                string tempZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OxideRust.zip");
                await File.WriteAllBytesAsync(tempZip, fileData);

                ZipFile.ExtractToDirectory(tempZip, _serverRoot, true);
                File.Delete(tempZip);

                PluginLog.AppendText($"[УСПЕХ]\n"); PluginLog.ScrollToEnd();
                RefreshInstalledPluginsList();
            }
            catch (Exception ex) { PluginLog.AppendText($"[ОШИБКА] {ex.Message}\n"); PluginLog.ScrollToEnd(); }
            finally { if (btn != null) btn.IsEnabled = true; }
        }

        private void SaveSettings_Click(object? sender, RoutedEventArgs? e)
        {
            if (string.IsNullOrEmpty(_cfgPath)) return;
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(_cfgPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"server.hostname \"{TxtServerName.Text}\"");
                sb.AppendLine($"server.maxplayers {TxtMaxPlayers.Text}");
                sb.AppendLine($"server.worldsize {TxtWorldSize.Text}");
                sb.AppendLine($"server.seed {TxtServerSeed.Text}");
                sb.AppendLine($"rcon.web 1");
                sb.AppendLine($"rcon.port 28016");
                sb.AppendLine($"rcon.password \"{TxtRconPassword.Text}\"");
                sb.AppendLine($"server.saveinterval 300");

                string customCmds = TxtCustomCommands.Text.Trim();
                if (!string.IsNullOrEmpty(customCmds)) sb.AppendLine(customCmds);

                File.WriteAllText(_cfgPath, sb.ToString(), new UTF8Encoding(false));
                if (e != null) MessageBox.Show("Сохранено!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { if (e != null) MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void LoadCfgSettings()
        {
            if (File.Exists(_cfgPath))
            {
                try
                {
                    string content = File.ReadAllText(_cfgPath);
                    var nameMatch = Regex.Match(content, @"server.hostname\s+""?([^""]+)""?"); if (nameMatch.Success) TxtServerName.Text = nameMatch.Groups[1].Value;
                    var maxMatch = Regex.Match(content, @"server.maxplayers\s+(\d+)"); if (maxMatch.Success) TxtMaxPlayers.Text = maxMatch.Groups[1].Value;
                    var worldMatch = Regex.Match(content, @"server.worldsize\s+(\d+)"); if (worldMatch.Success) TxtWorldSize.Text = worldMatch.Groups[1].Value;
                    var seedMatch = Regex.Match(content, @"server.seed\s+(\d+)"); if (seedMatch.Success) TxtServerSeed.Text = seedMatch.Groups[1].Value;
                    var passMatch = Regex.Match(content, @"rcon.password\s+""?([^""]+)""?"); if (passMatch.Success) TxtRconPassword.Text = passMatch.Groups[1].Value;

                    var knownKeys = new[] { "server.hostname", "server.maxplayers", "server.worldsize", "server.seed", "rcon.web", "rcon.port", "rcon.password", "server.saveinterval" };
                    var customLines = new List<string>();
                    foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!knownKeys.Any(k => line.TrimStart().StartsWith(k))) customLines.Add(line);
                    }
                    TxtCustomCommands.Text = string.Join(Environment.NewLine, customLines);
                }
                catch { }
            }
        }

        private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_cfgPath)) return;
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(_cfgPath);
                if (!string.IsNullOrEmpty(dir)) { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); Process.Start("explorer.exe", dir); }
            }
            catch { }
        }

        private async void ConnectRcon_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button; if (btn != null) btn.IsEnabled = false;
            try { RconLog.AppendText("Попытка подключения...\n"); RconLog.ScrollToEnd(); await _rcon.ConnectAsync(TxtRconIp.Text, int.Parse(TxtRconPort.Text), TxtRconPass.Password); }
            catch (Exception ex) { RconLog.AppendText($"Ошибка: {ex.Message}\n"); RconLog.ScrollToEnd(); }
            finally { if (btn != null) btn.IsEnabled = true; }
        }

        private void SendRconCommand_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtRconCommand.Text)) return;
            _rcon.SendCommand(TxtRconCommand.Text);
            TxtRconCommand.Clear();
        }

        private void TxtRconCommand_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { SendRconCommand_Click(sender, e); e.Handled = true; } }
        private void RefreshPlayers_Click(object sender, RoutedEventArgs e) => _rcon.RequestPlayerList();

        private void KickPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Player player)
            {
                _rcon.SendCommand($"kick {player.SteamId} \"Kicked by admin\"");
                RconLog.AppendText($"Игрок {player.Name} кикнут.\n"); RconLog.ScrollToEnd();
            }
        }

        private void BanPlayer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Player player)
            {
                _rcon.SendCommand($"ban {player.SteamId} \"Banned by admin\"");
                RconLog.AppendText($"Игрок {player.Name} забанен.\n"); RconLog.ScrollToEnd();
            }
        }
    }

    public class UmodPlugin
    {
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string Version { get; set; } = "";
        public string Slug { get; set; } = "";
    }

    public class InstalledPlugin
    {
        public string Name { get; set; } = "";
    }
}
