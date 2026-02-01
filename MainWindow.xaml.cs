using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace WeatherClock
{
    // 配置数据模型
    public class AppConfig
    {
        public string ApiKey { get; set; } = "";
        public string ApiHost { get; set; } = "simple.ai.qweatherapi.com";
        public string CustomCity { get; set; } = "";
        public bool IsLocked { get; set; } = false;
        public double ScaleFactor { get; set; } = 1.0;
        public double WinX { get; set; } = 100;
        public double WinY { get; set; } = 100;

        // 字体配置
        public string FontTime { get; set; } = "Arial";
        public string FontDate { get; set; } = "Arial";
        public string FontLoc { get; set; } = "Arial";
        public string FontWeather { get; set; } = "Arial";
        public string FontTemp { get; set; } = "Arial";

        // 缓存数据 (实现断网显示上次天气)
        public string LastLoc { get; set; } = "位置";
        public string LastTemp { get; set; } = "--";
        public string LastIcon { get; set; } = "☀";
    }

    public partial class MainWindow : Window
    {
        // WinAPI 窗口置底
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        const uint SWP_NOSIZE = 0x0001; const uint SWP_NOMOVE = 0x0002; const uint SWP_NOACTIVATE = 0x0010;

        private DispatcherTimer _timer;
        private DispatcherTimer _weatherTimer;
        private static HttpClient client;
        private WinForms.NotifyIcon _notifyIcon;
        private AppConfig _config;
        private string _configPath;

        // 日志锁，防止多线程同时写文件
        private static readonly object _logLock = new object();

        // 缩放交互状态
        private bool _isResizing = false;
        private System.Windows.Point _resizeStartPos;
        private double _initialScale;

        public MainWindow()
        {
            InitializeComponent();

            // 初始化 HttpClient
            var handler = new HttpClientHandler {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("User-Agent", "WeatherClock/2.0");

            // 配置文件路径 AppData/Roaming/WeatherClock/config.json
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configDir = Path.Combine(appData, "WeatherClock");
            if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
            _configPath = Path.Combine(configDir, "config.json");

            LoadConfig();
            InitializeTrayIcon();

            // 定时器
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            _timer.Start();

            _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _weatherTimer.Tick += async (s, e) => await UpdateWeather();
            _weatherTimer.Start();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // 恢复位置
            this.Left = _config.WinX;
            this.Top = _config.WinY;
            
            // 应用配置
            ApplyConfigToUI();
            
            // 恢复缓存数据显示 (防止启动时空白)
            LocationText.Text = _config.LastLoc;
            TempText.Text = $"{_config.LastTemp}°C";
            WeatherIcon.Text = _config.LastIcon;

            // 立即刷新时间与天气
            Timer_Tick(null, null);
            _ = UpdateWeather();
            SetWindowToBottom();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                else _config = new AppConfig();
            }
            catch { _config = new AppConfig(); }
        }

        private void SaveConfig()
        {
            try
            {
                _config.WinX = this.Left;
                _config.WinY = this.Top;
                string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            }
            catch { }
        }

        // 日志记录方法，用于排查问题
        private void LogError(string message)
        {
            // 添加锁防止多线程同时写文件
            lock (_logLock)
            {
                try
                {
                    string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WeatherClock");
                    if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                    string logPath = Path.Combine(logDir, "error.log");
                    
                    // 检查日志文件大小，如果超过1MB，清空日志
                    FileInfo logFile = new FileInfo(logPath);
                    if (logFile.Exists && logFile.Length > 1024 * 1024) // 1MB
                    {
                        File.WriteAllText(logPath, "", System.Text.Encoding.UTF8); // 清空日志
                    }
                    
                    using (StreamWriter writer = new StreamWriter(logPath, true, System.Text.Encoding.UTF8))
                    {
                        writer.WriteLine($"[{DateTime.Now}] {message}");
                    }
                }
                catch { }
            }
        }

        private void ApplyConfigToUI()
        {
            RootScale.ScaleX = _config.ScaleFactor;
            RootScale.ScaleY = _config.ScaleFactor;
            SetFontSafe(TimeText, _config.FontTime);
            SetFontSafe(DateText, _config.FontDate);
            SetFontSafe(LocationText, _config.FontLoc);
            SetFontSafe(WeatherIcon, _config.FontWeather);
            SetFontSafe(TempText, _config.FontTemp);
        }

        private void SetFontSafe(TextBlock tb, string fontName)
        {
            try { tb.FontFamily = new System.Windows.Media.FontFamily(fontName); } catch { }
        }

        // --- 交互逻辑 (拖拽移动 + 右键缩放) ---
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_config.IsLocked) return;

            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
                _config.WinX = this.Left;
                _config.WinY = this.Top;
                SaveConfig();
            }
            else if (e.ChangedButton == MouseButton.Right)
            {
                _isResizing = true;
                _resizeStartPos = e.GetPosition(this);
                _initialScale = _config.ScaleFactor;
                this.CaptureMouse();
            }
        }

        private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isResizing && e.RightButton == MouseButtonState.Pressed)
            {
                System.Windows.Point currentPos = e.GetPosition(this);
                double deltaY = _resizeStartPos.Y - currentPos.Y;
                // 灵敏度 200，限制缩放范围 0.5 - 5.0
                double newScale = Math.Max(0.5, Math.Min(5.0, _initialScale + (deltaY / 200.0)));
                
                _config.ScaleFactor = newScale;
                RootScale.ScaleX = newScale;
                RootScale.ScaleY = newScale;
            }
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isResizing)
            {
                _isResizing = false;
                this.ReleaseMouseCapture();
                SaveConfig();
            }
        }

        // --- 托盘菜单 ---
        private void InitializeTrayIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon();
            _notifyIcon.Text = "天气时钟";
            _notifyIcon.Visible = true;

            try
            {
                // 尝试从嵌入资源中加载图标
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                // 遍历所有资源名称，找到图标资源
                foreach (var resourceName in asm.GetManifestResourceNames())
                {
                    if (resourceName.EndsWith("clock.ico", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var stream = asm.GetManifestResourceStream(resourceName))
                        {
                            if (stream != null)
                            {
                                _notifyIcon.Icon = new Drawing.Icon(stream);
                                break;
                            }
                        }
                    }
                }
                // 如果没有找到资源，使用系统默认图标
                if (_notifyIcon.Icon == null)
                {
                    _notifyIcon.Icon = Drawing.SystemIcons.Application;
                }
            }
            catch { _notifyIcon.Icon = Drawing.SystemIcons.Application; }

            var contextMenu = new WinForms.ContextMenuStrip();

            contextMenu.Items.Add("设置", null, (s, e) => ShowSettingsDialog());

            var lockItem = new WinForms.ToolStripMenuItem("锁定位置/缩放");
            lockItem.CheckOnClick = true;
            lockItem.Checked = _config.IsLocked;
            lockItem.Click += (s, e) => { _config.IsLocked = lockItem.Checked; SaveConfig(); };
            contextMenu.Items.Add(lockItem);

            contextMenu.Items.Add(new WinForms.ToolStripSeparator());

            var fontMenu = new WinForms.ToolStripMenuItem("字体设置");
            fontMenu.DropDownItems.Add("时间字体", null, (s, e) => ChangeFont(TimeText, "FontTime"));
            fontMenu.DropDownItems.Add("日期字体", null, (s, e) => ChangeFont(DateText, "FontDate"));
            fontMenu.DropDownItems.Add("位置字体", null, (s, e) => ChangeFont(LocationText, "FontLoc"));
            fontMenu.DropDownItems.Add("温度字体", null, (s, e) => ChangeFont(TempText, "FontTemp"));
            fontMenu.DropDownItems.Add("天气图标字体", null, (s, e) => ChangeFont(WeatherIcon, "FontWeather"));
            contextMenu.Items.Add(fontMenu);

            contextMenu.Items.Add(new WinForms.ToolStripSeparator());

            var autoStartItem = new WinForms.ToolStripMenuItem("开机自启");
            autoStartItem.CheckOnClick = true;
            autoStartItem.Checked = IsAutoStartEnabled();
            autoStartItem.Click += (s, e) => ToggleAutoStart(autoStartItem.Checked);
            contextMenu.Items.Add(autoStartItem);

            contextMenu.Items.Add("刷新天气", null, (s, e) => { LocationText.Text = "刷新..."; _ = UpdateWeather(); });
            
            contextMenu.Items.Add(new WinForms.ToolStripSeparator());
            
            contextMenu.Items.Add("退出", null, (s, e) => {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                System.Windows.Application.Current.Shutdown();
            });

            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        // --- 设置窗口 ---
        private void ShowSettingsDialog()
        {
            Window w = new Window { Title = "设置", Width = 320, Height = 280, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize, Topmost = true };
            StackPanel sp = new StackPanel { Margin = new Thickness(15) };
            
            sp.Children.Add(new TextBlock { Text = "和风天气 API Key:", Margin = new Thickness(0,0,0,5) });
            System.Windows.Controls.TextBox tbKey = new System.Windows.Controls.TextBox { Text = _config.ApiKey, Height = 25 };
            sp.Children.Add(tbKey);

            sp.Children.Add(new TextBlock { Text = "API Host:", Margin = new Thickness(0,10,0,5) });
            System.Windows.Controls.TextBox tbHost = new System.Windows.Controls.TextBox { Text = _config.ApiHost, Height = 25 };
            sp.Children.Add(tbHost);

            sp.Children.Add(new TextBlock { Text = "自定义城市 (留空自动定位):", Margin = new Thickness(0,10,0,5) });
            System.Windows.Controls.TextBox tbCity = new System.Windows.Controls.TextBox { Text = _config.CustomCity, Height = 25 };
            sp.Children.Add(tbCity);

            System.Windows.Controls.Button btn = new System.Windows.Controls.Button { Content = "保存并刷新", Height = 35, Margin = new Thickness(0,20,0,0) };
            btn.Click += (s, e) => {
                _config.ApiKey = tbKey.Text.Trim();
                _config.ApiHost = string.IsNullOrEmpty(tbHost.Text) ? "simple.ai.qweatherapi.com" : tbHost.Text.Trim();
                _config.CustomCity = tbCity.Text.Trim();
                SaveConfig();
                w.Close();
                LocationText.Text = "刷新配置...";
                _ = UpdateWeather();
            };
            sp.Children.Add(btn);

            w.Content = sp;
            w.ShowDialog();
        }

        // --- 字体修改 ---
        private void ChangeFont(TextBlock target, string configKey)
        {
            var fd = new WinForms.FontDialog();
            try { fd.Font = new Drawing.Font(target.FontFamily.Source, (float)target.FontSize); } catch {}

            if (fd.ShowDialog() == WinForms.DialogResult.OK)
            {
                string fontName = fd.Font.Name;
                target.FontFamily = new System.Windows.Media.FontFamily(fontName);
                typeof(AppConfig).GetProperty(configKey)?.SetValue(_config, fontName);
                SaveConfig();
            }
        }

        // --- 注册表自启 ---
        private bool IsAutoStartEnabled() {
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                    return key?.GetValue("WeatherClock") != null;
            } catch { return false; }
        }

        private void ToggleAutoStart(bool enable) {
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) {
                    if (enable) {
                        // Environment.ProcessPath 在 .NET 6+ 可用，比 Process 更准确
                        string exe = Environment.ProcessPath; 
                        if (exe != null && exe.EndsWith(".exe")) key.SetValue("WeatherClock", $"\"{exe}\"");
                    } else key.DeleteValue("WeatherClock", false);
                }
            } catch { System.Windows.MessageBox.Show("无法设置注册表，请检查权限。"); }
        }

        // --- 时间与置底 ---
        private void Timer_Tick(object? sender, EventArgs? e) {
            var now = DateTime.Now;
            TimeText.Text = now.ToString("HH:mm");
            DateText.Text = now.ToString("MM月dd日 dddd");
            
            // 每5秒检查置底，且避免在交互时触发以防闪烁
            if (now.Second % 5 == 0 && Mouse.LeftButton != MouseButtonState.Pressed && !_isResizing) 
                SetWindowToBottom();
        }

        private void SetWindowToBottom() {
            try {
                IntPtr hWnd = new WindowInteropHelper(this).Handle;
                SetWindowPos(hWnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            } catch { }
        }

        private void RefreshWeather_Click(object sender, MouseButtonEventArgs e) {
            if (string.IsNullOrEmpty(_config.ApiKey)) ShowSettingsDialog();
            else { LocationText.Text = "刷新..."; _ = UpdateWeather(); }
        }

        // --- 天气业务逻辑 ---
        private async System.Threading.Tasks.Task UpdateWeather() {
            LogError("=== 开始更新天气 ===");
            
            if (string.IsNullOrEmpty(_config.ApiKey)) {
                LocationText.Text = "请配置Key";
                LogError("API Key为空，请配置");
                return;
            }
            
            LogError("API Key: " + _config.ApiKey);
            LogError("API Host: " + _config.ApiHost);
            LogError("Custom City: " + _config.CustomCity);
            
            try {
                string locationId = "";
                
                // 1. 优先自定义城市
                LogError("1. 优先自定义城市");
                if (!string.IsNullOrEmpty(_config.CustomCity)) {
                    LogError("尝试获取自定义城市的Location ID: " + _config.CustomCity);
                    locationId = await GetLocationId(_config.CustomCity);
                    if (string.IsNullOrEmpty(locationId)) {
                        LocationText.Text = "未找到城市";
                        LogError("GetLocationId returned empty for custom city: " + _config.CustomCity);
                        return;
                    }
                    LogError("获取到自定义城市的Location ID: " + locationId);
                }
                
                // 2. IP 自动定位
                if (string.IsNullOrEmpty(locationId)) {
                    LogError("2. IP 自动定位");
                    locationId = await GetAutoLocationId();
                    if (string.IsNullOrEmpty(locationId)) {
                        LocationText.Text = "定位失败";
                        LogError("GetAutoLocationId returned empty");
                        return;
                    }
                    LogError("IP自动定位获取到的Location ID: " + locationId);
                }

                // 3. 查天气 - 使用X-QW-Api-Key请求头
                LogError("3. 查天气");
                string url = $"https://{_config.ApiHost}/v7/weather/now?location={locationId}";
                LogError("天气API URL: " + url);
                
                // 创建HTTP请求
                LogError("创建HTTP请求");
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-QW-Api-Key", _config.ApiKey);
                LogError("添加请求头: X-QW-Api-Key=" + _config.ApiKey);
                
                // 发送请求
                LogError("发送HTTP请求");
                var response = await client.SendAsync(request);
                LogError("HTTP响应状态码: " + response.StatusCode);
                LogError("HTTP响应内容长度: " + response.Content.Headers.ContentLength);
                
                // 确保响应成功
                try {
                    response.EnsureSuccessStatusCode();
                    LogError("HTTP响应成功");
                } catch (HttpRequestException ex) {
                    LogError("HTTP请求失败: " + ex.Message);
                    LogError("HTTP响应内容: " + await response.Content.ReadAsStringAsync());
                    throw;
                }
                
                // 读取响应
                LogError("读取HTTP响应内容");
                string json = await response.Content.ReadAsStringAsync();
                LogError("接收到的天气响应: " + json);
                
                // 解析响应
                LogError("解析天气响应");
                using (JsonDocument doc = JsonDocument.Parse(json)) {
                    if (doc.RootElement.TryGetProperty("code", out var codeProp)) {
                        string code = codeProp.GetString();
                        LogError("天气API返回码: " + code);
                        
                        if (code == "200") {
                            LogError("天气API返回码为200，成功");
                            if (doc.RootElement.TryGetProperty("now", out var nowProp)) {
                                LogError("响应包含now字段");
                                if (nowProp.TryGetProperty("temp", out var tempProp) && nowProp.TryGetProperty("icon", out var iconProp)) {
                                    string temp = tempProp.GetString();
                                    string iconCode = iconProp.GetString();
                                    LogError("获取到温度: " + temp);
                                    LogError("获取到图标代码: " + iconCode);
                                    
                                    // 更新UI
                                    TempText.Text = $"{temp}°C";
                                    WeatherIcon.Text = GetIcon(iconCode);
                                    LogError("更新UI成功");
                                    
                                    // 缓存数据
                                    _config.LastTemp = temp;
                                    _config.LastIcon = WeatherIcon.Text;
                                    _config.LastLoc = LocationText.Text;
                                    SaveConfig();
                                    LogError("缓存数据成功");
                                    LogError("天气更新完成: " + temp + "°C, icon: " + iconCode);
                                } else {
                                    LocationText.Text = "数据格式错误";
                                    LogError("响应缺少temp或icon字段");
                                    // 打印now字段的所有属性
                                    LogError("now字段内容: " + nowProp.ToString());
                                }
                            } else {
                                LocationText.Text = "数据格式错误";
                                LogError("响应缺少now字段");
                                // 打印整个响应的结构
                                LogError("响应结构: " + doc.RootElement.ToString());
                            }
                        } else {
                            LocationText.Text = "API错误: " + code;
                            LogError("天气API返回错误码: " + code);
                            // 打印整个响应的结构
                            LogError("错误响应: " + doc.RootElement.ToString());
                        }
                    } else {
                        LocationText.Text = "数据格式错误";
                        LogError("响应缺少code字段");
                        // 打印整个响应的结构
                        LogError("响应结构: " + doc.RootElement.ToString());
                    }
                }
            } catch (Exception ex) {
                string errorMsg = "离线: " + ex.Message.Substring(0, Math.Min(ex.Message.Length, 20));
                LocationText.Text = errorMsg;
                LogError("UpdateWeather异常: " + ex.Message);
                LogError("异常堆栈: " + ex.StackTrace);
            } finally {
                LogError("=== 天气更新结束 ===");
            }
        }

        private async System.Threading.Tasks.Task<string> GetLocationId(string cityName) {
            LogError("=== 开始获取Location ID ===");
            LogError("City Name: " + cityName);
            
            try {
                LocationText.Text = cityName;
                // 使用正确的地理编码API地址和X-QW-Api-Key请求头
                string url = $"https://{_config.ApiHost}/geo/v2/city/lookup?location={cityName}";
                LogError("地理编码API URL: " + url);
                
                // 创建HTTP请求
                LogError("创建HTTP请求");
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-QW-Api-Key", _config.ApiKey);
                LogError("添加请求头: X-QW-Api-Key=" + _config.ApiKey);
                
                // 发送请求
                LogError("发送HTTP请求");
                var response = await client.SendAsync(request);
                LogError("HTTP响应状态码: " + response.StatusCode);
                
                // 确保响应成功
                try {
                    response.EnsureSuccessStatusCode();
                    LogError("HTTP响应成功");
                } catch (HttpRequestException ex) {
                    LogError("HTTP请求失败: " + ex.Message);
                    LogError("HTTP响应内容: " + await response.Content.ReadAsStringAsync());
                    throw;
                }
                
                // 读取响应
                LogError("读取HTTP响应内容");
                string json = await response.Content.ReadAsStringAsync();
                LogError("接收到的地理编码响应: " + json);
                
                // 解析响应
                LogError("解析地理编码响应");
                using (JsonDocument doc = JsonDocument.Parse(json)) {
                    if (doc.RootElement.TryGetProperty("code", out var codeProp)) {
                        string code = codeProp.GetString();
                        LogError("地理编码API返回码: " + code);
                        
                        if (code == "200") {
                            LogError("地理编码API返回码为200，成功");
                            if (doc.RootElement.TryGetProperty("location", out var locationProp)) {
                                LogError("响应包含location字段");
                                int locationCount = locationProp.GetArrayLength();
                                LogError("location数组长度: " + locationCount);
                                
                                if (locationCount > 0) {
                                    LogError("location数组非空，获取第一个元素");
                                    var firstLocation = locationProp[0];
                                    LogError("第一个location元素: " + firstLocation.ToString());
                                    
                                    if (firstLocation.TryGetProperty("id", out var idProp)) {
                                        string id = idProp.GetString();
                                        LogError("获取到Location ID: " + id);
                                        LogError("=== 获取Location ID结束 ===");
                                        return id;
                                    } else {
                                        LogError("第一个location元素缺少id字段");
                                    }
                                } else {
                                    LogError("location数组为空");
                                }
                            } else {
                                LogError("响应缺少location字段");
                                // 打印整个响应的结构
                                LogError("响应结构: " + doc.RootElement.ToString());
                            }
                        } else {
                            LogError("地理编码API返回错误码: " + code);
                            // 打印整个响应的结构
                            LogError("错误响应: " + doc.RootElement.ToString());
                        }
                    } else {
                        LogError("响应缺少code字段");
                        // 打印整个响应的结构
                        LogError("响应结构: " + doc.RootElement.ToString());
                    }
                }
            } catch (Exception ex) {
                LogError("GetLocationId异常: " + ex.Message);
                LogError("异常堆栈: " + ex.StackTrace);
            } finally {
                LogError("=== 获取Location ID结束 ===");
            }
            LogError("GetLocationId returned empty");
            return "";
        }

        private async System.Threading.Tasks.Task<string> GetAutoLocationId() {
            LogError("=== 开始IP自动定位 ===");
            
            try {
                // 尝试使用ip9.com.cn获取IP定位
                LogError("1. 尝试使用ip9.com.cn获取IP定位");
                string url = "https://ip9.com.cn/get";
                LogError("IP定位API URL: " + url);
                
                string json = await client.GetStringAsync(url);
                LogError("接收到的IP定位响应: " + json);
                
                using (JsonDocument doc = JsonDocument.Parse(json)) {
                    LogError("解析IP定位响应");
                    if (doc.RootElement.TryGetProperty("data", out var dataProp)) {
                        LogError("响应包含data字段");
                        if (dataProp.TryGetProperty("city", out var cityProp)) {
                            string city = cityProp.GetString();
                            LogError("从IP定位获取到城市: " + city);
                            
                            // 获取城市的Location ID
                            string locationId = await GetLocationId(city);
                            if (!string.IsNullOrEmpty(locationId)) {
                                LogError("IP自动定位成功，获取到Location ID: " + locationId);
                                LogError("=== IP自动定位结束 ===");
                                return locationId;
                            } else {
                                LogError("获取城市的Location ID失败");
                            }
                        } else {
                            LogError("响应缺少city字段");
                            // 打印data字段的所有属性
                            LogError("data字段内容: " + dataProp.ToString());
                        }
                    } else {
                        LogError("响应缺少data字段");
                        // 打印整个响应的结构
                        LogError("响应结构: " + doc.RootElement.ToString());
                    }
                }
            } catch (Exception ex) {
                LogError("GetAutoLocationId异常: " + ex.Message);
                LogError("异常堆栈: " + ex.StackTrace);
            }
            
            // 如果自动定位失败，返回北京的locationId作为默认值
            LogError("2. 自动定位失败，返回北京的Location ID作为默认值");
            LogError("=== IP自动定位结束 ===");
            return "101010100";
        }

        private string GetIcon(string c) {
            if (c == "100" || c == "150") return "☀";
            if (c == "101" || c == "102" || c == "103" || c == "151" || c == "152") return "⛅";
            if (c == "104" || c == "153" || c == "800" || c == "801" || c == "802") return "☁";
            if (c.StartsWith("3")) return (c == "302" || c == "303" || c == "304") ? "⛈" : "🌧";
            if (c.StartsWith("4")) return "🌨";
            if (c.StartsWith("5")) return "🌫";
            return "☁";
        }
    }
}