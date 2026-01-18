# weather_clock.py - 主程序文件

import sys
import json
import os
import requests
import ctypes
from datetime import datetime
from PyQt5.QtWidgets import (QApplication, QWidget, QLabel, QVBoxLayout, 
                             QSystemTrayIcon, QMenu, QAction, QDialog, 
                             QLineEdit, QPushButton, QHBoxLayout,
                             QFontDialog, QInputDialog)
from PyQt5.QtCore import QTimer, Qt, QPoint
from PyQt5.QtGui import QIcon, QFont

class SettingsDialog(QDialog):
    """设置对话框"""
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("设置")
        self.setModal(True)
        
        layout = QVBoxLayout()
        
        # API Key输入
        api_layout = QHBoxLayout()
        api_layout.addWidget(QLabel("和风天气API Key:"))
        self.api_input = QLineEdit()
        self.api_input.setText(parent.config.get('api_key', ''))
        api_layout.addWidget(self.api_input)
        layout.addLayout(api_layout)
        
        # API Host输入
        host_layout = QHBoxLayout()
        host_layout.addWidget(QLabel("API Host:"))
        self.host_input = QLineEdit()
        self.host_input.setText(parent.config.get('api_host', ''))
        self.host_input.setPlaceholderText("例如: simple.ai.qweatherapi.com")
        host_layout.addWidget(self.host_input)
        layout.addLayout(host_layout)
        
        # 城市输入
        city_layout = QHBoxLayout()
        city_layout.addWidget(QLabel("自定义城市:"))
        self.city_input = QLineEdit()
        self.city_input.setPlaceholderText("留空则自动获取位置")
        self.city_input.setText(parent.config.get('custom_city', ''))
        city_layout.addWidget(self.city_input)
        layout.addLayout(city_layout)
        
        # 保存按钮
        save_btn = QPushButton("保存")
        save_btn.clicked.connect(self.accept)
        layout.addWidget(save_btn)
        
        self.setLayout(layout)

class WeatherClock(QWidget):
    def __init__(self):
        super().__init__()
        
        # 配置文件路径
        self.config_file = os.path.join(os.path.expanduser('~'), '.weather_clock_config.json')
        self.config = self.load_config()
        
        # 天气数据缓存
        self.last_weather_data = self.config.get('last_weather_data', {})
        
        # 初始化UI
        self.init_ui()
        
        # 创建托盘图标
        self.create_tray_icon()
        
        # 启动定时器
        self.timer = QTimer()
        self.timer.timeout.connect(self.update_time)
        self.timer.start(1000)  # 每秒更新时间
        
        # 天气更新定时器（每30分钟）
        self.weather_timer = QTimer()
        self.weather_timer.timeout.connect(self.update_weather)
        self.weather_timer.start(1800000)
        
        # 初始更新
        self.update_time()
        self.update_weather()
        
        # 拖动相关变量
        self.dragging = False
        self.drag_position = QPoint()
        self.resizing = False
        self.resize_start_pos = QPoint()
        self.initial_scale = 1.0
        
    def init_ui(self):
        """初始化UI"""
        self.setWindowFlags(Qt.FramelessWindowHint | Qt.Tool)
        self.setAttribute(Qt.WA_TranslucentBackground)
        
        # 设置窗口图标
        icon_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'clock.ico')
        if os.path.exists(icon_path):
            self.setWindowIcon(QIcon(icon_path))
        
        # 主布局
        layout = QVBoxLayout()
        layout.setContentsMargins(10, 10, 10, 10)
        
        # 时间标签
        self.time_label = QLabel("00:00")
        time_font = QFont(self.config.get('time_font', 'Arial'), 
                         self.config.get('time_size', 48), 
                         QFont.Bold)
        self.time_label.setFont(time_font)
        self.time_label.setStyleSheet("color: white; background: transparent;")
        self.time_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.time_label)
        
        # 日期标签
        self.date_label = QLabel("01月01日 星期一")
        date_font = QFont(self.config.get('date_font', 'Arial'), 
                         self.config.get('date_size', 16))
        self.date_label.setFont(date_font)
        self.date_label.setStyleSheet("color: white; background: transparent;")
        self.date_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.date_label)
        
        # 城市/天气/温度 并排显示
        weather_layout = QHBoxLayout()
        weather_layout.setSpacing(10)
        
        # 位置标签
        self.location_label = QLabel("位置")
        location_font = QFont(self.config.get('location_font', 'Arial'), 
                            self.config.get('location_size', 14))
        self.location_label.setFont(location_font)
        self.location_label.setStyleSheet("color: white; background: transparent;")
        weather_layout.addWidget(self.location_label)
        
        # 天气图标
        self.weather_icon_label = QLabel("☀")
        weather_icon_font = QFont(self.config.get('weather_font', 'Arial'), 
                                 self.config.get('weather_size', 24))
        self.weather_icon_label.setFont(weather_icon_font)
        self.weather_icon_label.setStyleSheet("color: white; background: transparent;")
        self.weather_icon_label.mousePressEvent = self.refresh_weather
        weather_layout.addWidget(self.weather_icon_label)
        
        # 温度标签
        self.temp_label = QLabel("--°C")
        temp_font = QFont(self.config.get('temp_font', 'Arial'), 
                         self.config.get('temp_size', 16))
        self.temp_label.setFont(temp_font)
        self.temp_label.setStyleSheet("color: white; background: transparent;")
        weather_layout.addWidget(self.temp_label)
        
        layout.addLayout(weather_layout)
        
        self.setLayout(layout)
        
        # 恢复窗口位置
        pos = self.config.get('window_pos', [100, 100])
        self.move(pos[0], pos[1])
        
        # 应用缩放
        self.scale_factor = self.config.get('scale_factor', 1.0)
        self.apply_scale()
        
        self.show()
        
        # 设置窗口到最底层（在show()之后调用）
        self.set_window_bottom()
    
    def set_window_bottom(self):
        """设置窗口到最底层"""
        hwnd = int(self.winId())
        ctypes.windll.user32.SetWindowPos(hwnd, 1, 0, 0, 0, 0, 0x0013)
    
    def apply_scale(self):
        """应用缩放因子"""
        self.time_label.setFont(QFont(
            self.config.get('time_font', 'Arial'),
            int(self.config.get('time_size', 48) * self.scale_factor),
            QFont.Bold
        ))
        self.date_label.setFont(QFont(
            self.config.get('date_font', 'Arial'),
            int(self.config.get('date_size', 16) * self.scale_factor)
        ))
        self.location_label.setFont(QFont(
            self.config.get('location_font', 'Arial'),
            int(self.config.get('location_size', 14) * self.scale_factor)
        ))
        self.weather_icon_label.setFont(QFont(
            self.config.get('weather_font', 'Arial'),
            int(self.config.get('weather_size', 24) * self.scale_factor)
        ))
        self.temp_label.setFont(QFont(
            self.config.get('temp_font', 'Arial'),
            int(self.config.get('temp_size', 16) * self.scale_factor)
        ))
        self.adjustSize()
    
    def mousePressEvent(self, event):
        """鼠标按下事件"""
        if event.button() == Qt.LeftButton:
            if ':' in self.time_label.text() and not self.config.get('locked', False):
                cursor_pos = event.pos()
                label_rect = self.time_label.geometry()
                if label_rect.contains(cursor_pos):
                    self.dragging = True
                    self.drag_position = event.globalPos() - self.frameGeometry().topLeft()
                    event.accept()
        elif event.button() == Qt.RightButton:
            if not self.config.get('locked', False):
                self.resizing = True
                self.resize_start_pos = event.globalPos()
                self.initial_scale = self.scale_factor
                event.accept()
    
    def mouseMoveEvent(self, event):
        """鼠标移动事件"""
        if self.dragging and event.buttons() == Qt.LeftButton:
            self.move(event.globalPos() - self.drag_position)
            event.accept()
        elif self.resizing and event.buttons() == Qt.RightButton:
            delta = event.globalPos().y() - self.resize_start_pos.y()
            new_scale = max(0.5, min(3.0, self.initial_scale - delta / 200.0))
            self.scale_factor = new_scale
            self.apply_scale()
            event.accept()
    
    def mouseReleaseEvent(self, event):
        """鼠标释放事件"""
        if event.button() == Qt.LeftButton and self.dragging:
            self.dragging = False
            self.save_window_position()
        elif event.button() == Qt.RightButton and self.resizing:
            self.resizing = False
            self.config['scale_factor'] = self.scale_factor
            self.save_config()
    
    def refresh_weather(self, event):
        """点击天气图标刷新"""
        self.update_weather()
    
    def create_tray_icon(self):
        """创建系统托盘图标"""
        self.tray_icon = QSystemTrayIcon(self)
        
        icon_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'clock.ico')
        if os.path.exists(icon_path):
            self.tray_icon.setIcon(QIcon(icon_path))
        else:
            self.tray_icon.setIcon(self.style().standardIcon(self.style().SP_ComputerIcon))
        
        tray_menu = QMenu()
        
        self.autostart_action = QAction("开机自启动", self, checkable=True)
        self.autostart_action.setChecked(self.config.get('autostart', False))
        self.autostart_action.triggered.connect(self.toggle_autostart)
        tray_menu.addAction(self.autostart_action)
        
        self.lock_action = QAction("锁定位置/缩放", self, checkable=True)
        self.lock_action.setChecked(self.config.get('locked', False))
        self.lock_action.triggered.connect(self.toggle_lock)
        tray_menu.addAction(self.lock_action)
        
        tray_menu.addSeparator()
        
        font_menu = tray_menu.addMenu("自定义字体")
        
        element_names = {'time':'时间','date':'日期','location':'位置','weather':'天气','temp':'温度'}
        for element, name in element_names.items():
            action = QAction(f"{name}字体", self)
            action.triggered.connect(lambda checked, e=element: self.change_font(e))
            font_menu.addAction(action)
        
        size_menu = tray_menu.addMenu("调整大小")
        
        size_options = [
            ("时间大小", 'time_size', 48),
            ("日期大小", 'date_size', 16),
            ("位置大小", 'location_size', 14),
            ("天气大小", 'weather_size', 24),
            ("温度大小", 'temp_size', 16)
        ]
        
        for label, key, default in size_options:
            action = QAction(label, self)
            action.triggered.connect(lambda checked, k=key, d=default: self.change_size(k, d))
            size_menu.addAction(action)
        
        tray_menu.addSeparator()
        
        settings_action = QAction("设置", self)
        settings_action.triggered.connect(self.show_settings)
        tray_menu.addAction(settings_action)
        
        quit_action = QAction("退出", self)
        quit_action.triggered.connect(self.quit_application)
        tray_menu.addAction(quit_action)
        
        self.tray_icon.setContextMenu(tray_menu)
        self.tray_icon.show()
    
    def change_font(self, element):
        """更改字体"""
        current_font = QFont(
            self.config.get(f'{element}_font', 'Arial'),
            self.config.get(f'{element}_size', 16)
        )
        font, ok = QFontDialog.getFont(current_font, self)
        if ok:
            self.config[f'{element}_font'] = font.family()
            self.save_config()
            self.apply_scale()
    
    def change_size(self, key, default):
        """更改大小"""
        current = self.config.get(key, default)
        value, ok = QInputDialog.getInt(self, "调整大小", f"输入新的大小:", current, 8, 200)
        if ok:
            self.config[key] = value
            self.save_config()
            self.apply_scale()
    
    def toggle_autostart(self):
        """切换自启动状态"""
        enabled = self.autostart_action.isChecked()
        self.config['autostart'] = enabled
        self.save_config()
        
        if enabled:
            self.enable_autostart()
        else:
            self.disable_autostart()
    
    def toggle_lock(self):
        """切换锁定状态"""
        self.config['locked'] = self.lock_action.isChecked()
        self.save_config()
    
    def enable_autostart(self):
        """启用开机自启动"""
        import winreg
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Run",
                            0, winreg.KEY_SET_VALUE)
        winreg.SetValueEx(key, "WeatherClock", 0, winreg.REG_SZ, 
                         f'"{sys.executable}" "{os.path.abspath(__file__)}"')
        winreg.CloseKey(key)
    
    def disable_autostart(self):
        """禁用开机自启动"""
        import winreg
        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                                r"Software\Microsoft\Windows\CurrentVersion\Run",
                                0, winreg.KEY_SET_VALUE)
            winreg.DeleteValue(key, "WeatherClock")
            winreg.CloseKey(key)
        except:
            pass
    
    def show_settings(self):
        """显示设置对话框"""
        dialog = SettingsDialog(self)
        if dialog.exec_():
            self.config['api_key'] = dialog.api_input.text()
            self.config['api_host'] = dialog.host_input.text()
            self.config['custom_city'] = dialog.city_input.text()
            self.save_config()
            self.update_weather()
    
    def update_time(self):
        """更新时间显示"""
        now = datetime.now()
        self.time_label.setText(now.strftime("%H:%M"))
        
        weekdays = ['星期一', '星期二', '星期三', '星期四', '星期五', '星期六', '星期日']
        weekday = weekdays[now.weekday()]
        self.date_label.setText(now.strftime(f"%m月%d日 {weekday}"))
    
    def update_weather(self):
        """更新天气信息"""
        api_key = self.config.get('api_key', '')
        api_host = self.config.get('api_host', '')
        
        if not api_key or not api_host:
            self.location_label.setText("请设置API")
            return
        
        try:
            # 设置请求头
            headers = {
                'X-QW-Api-Key': api_key,
                'User-Agent': 'Mozilla/5.0'
            }
            
            # 获取位置
            custom_city = self.config.get('custom_city', '')
            if custom_city:
                location = self.get_location_id(custom_city, api_host, headers)
            else:
                location = self.get_auto_location(api_host, headers)
            
            if not location:
                self.use_cached_weather()
                return
            
            # 获取天气
            weather_url = f"https://{api_host}/v7/weather/now?location={location['id']}"
            response = requests.get(weather_url, headers=headers, timeout=10)
            data = response.json()
            
            if data['code'] == '200':
                weather = data['now']
                self.location_label.setText(location['name'])
                self.temp_label.setText(f"{weather['temp']}°C")
                
                icon_map = {
                    '100': '☀', '101': '⛅', '102': '⛅', '103': '☁', '104': '☁',
                    '150': '☀', '151': '⛅', '152': '⛅', '153': '☁',
                    '300': '🌧', '301': '🌧', '302': '⛈', '303': '⛈',
                    '400': '🌨', '401': '🌨', '402': '🌨', '403': '🌨',
                    '500': '🌫', '501': '🌫',
                }
                icon = icon_map.get(weather['icon'], '☀')
                self.weather_icon_label.setText(icon)
                
                self.last_weather_data = {
                    'location': location['name'],
                    'temp': weather['temp'],
                    'icon': icon
                }
                self.config['last_weather_data'] = self.last_weather_data
                self.save_config()
            else:
                self.use_cached_weather()
                
        except Exception:
            self.use_cached_weather()
    
    def use_cached_weather(self):
        """使用缓存的天气数据"""
        if self.last_weather_data:
            self.location_label.setText(self.last_weather_data.get('location', '离线'))
            self.temp_label.setText(f"{self.last_weather_data.get('temp', '--')}°C")
            self.weather_icon_label.setText(self.last_weather_data.get('icon', '☀'))
        else:
            self.location_label.setText("离线")
    
    def get_auto_location(self, api_host, headers):
        """自动获取位置 - 使用ip9.com.cn"""
        try:
            # 使用ip9.com.cn获取城市名
            ip_response = requests.get('https://ip9.com.cn/get', timeout=5)
            
            if ip_response.status_code == 200:
                ip_data = ip_response.json()
                
                if ip_data.get('ret') == 200:
                    data = ip_data.get('data', {})
                    city_name = data.get('city', '')
                    
                    if city_name:
                        # 用获取到的城市名查询和风天气
                        return self.get_location_id(city_name, api_host, headers)
        except Exception:
            pass
        
        return None
    
    def get_location_id(self, city_name, api_host, headers):
        """根据城市名获取位置ID"""
        try:
            url = f"https://{api_host}/geo/v2/city/lookup?location={city_name}"
            response = requests.get(url, headers=headers, timeout=10)
            data = response.json()
            
            if data['code'] == '200' and data.get('location'):
                loc = data['location'][0]
                return {'id': loc['id'], 'name': loc['name']}
        except Exception:
            pass
        return None
    
    def load_config(self):
        """加载配置"""
        if os.path.exists(self.config_file):
            try:
                with open(self.config_file, 'r', encoding='utf-8') as f:
                    return json.load(f)
            except:
                pass
        return {}
    
    def save_config(self):
        """保存配置"""
        with open(self.config_file, 'w', encoding='utf-8') as f:
            json.dump(self.config, f, ensure_ascii=False, indent=2)
    
    def save_window_position(self):
        """保存窗口位置"""
        pos = self.pos()
        self.config['window_pos'] = [pos.x(), pos.y()]
        self.save_config()
    
    def quit_application(self):
        """退出应用"""
        self.save_window_position()
        QApplication.quit()
    
    def closeEvent(self, event):
        """关闭事件 - 最小化到托盘"""
        event.ignore()
        self.hide()
        self.tray_icon.showMessage(
            "天气时钟",
            "程序已最小化到托盘",
            QSystemTrayIcon.Information,
            2000
        )

if __name__ == '__main__':
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    clock = WeatherClock()
    sys.exit(app.exec_())