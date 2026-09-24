using System;
using System.IO;
using System.Text.Json;

namespace Glance.Services;

/// <summary>
/// Service for persisting app settings (theme) to a JSON file
/// in the app's own directory (portable/green mode).
/// </summary>
public class SettingsService
{
    // 数据落在程序本体目录的 GlanceData 子目录（便携版硬约束：不写 C 盘 AppData / 系统 Temp）。
    private static readonly string SettingsFilePath = AppPaths.SettingsFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private SettingsData _data;

    public SettingsService()
    {
        _data = new SettingsData();
        Load();
    }

    public string AppTheme
    {
        get => _data.AppTheme;
        set
        {
            if (_data.AppTheme != value)
            {
                _data.AppTheme = value;
                Save();
            }
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            string json = File.ReadAllText(SettingsFilePath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data != null)
            {
                _data = data;
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(_data, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch { }
    }

    private class SettingsData
    {
        public string AppTheme { get; set; } = "Default";
    }
}