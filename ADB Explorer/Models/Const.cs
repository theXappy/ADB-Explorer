﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

namespace ADB_Explorer.Models
{
    public static class AdbExplorerConst
    {
        public static readonly string DEFAULT_PATH = "/sdcard";
        public static readonly Dictionary<string, string> SPECIAL_FOLDERS_PRETTY_NAMES = new()
        {
            { "/sdcard", "Internal Storage" },
            { "/storage/emulated/0", "Internal Storage" },
            { "/storage/self/primary", "Internal Storage" },
            { "/mnt/sdcard", "Internal Storage" },
            { "/", "Root" }
        };

        public static readonly Dictionary<string, DriveType> DRIVE_TYPES = new()
        {
            { "/storage/emulated", DriveType.Internal },
            { "/sdcard", DriveType.Internal },
            { "/", DriveType.Root }
        };
        public static readonly Dictionary<DriveType, string> DRIVES_PRETTY_NAMES = new()
        {
            { DriveType.Root, "Root" },
            { DriveType.Internal, "Internal Storage" },
            { DriveType.Expansion, "µSD Card" },
            { DriveType.External, "OTG Drive" },
            { DriveType.Unknown, "" }, // "Other Drive"
            { DriveType.Emulated, "Emulated Drive" }
        };

        public static readonly TimeSpan DIR_LIST_UPDATE_INTERVAL = TimeSpan.FromMilliseconds(1000);
        public static readonly TimeSpan SYNC_PROG_UPDATE_INTERVAL = TimeSpan.FromMilliseconds(100);
        public static readonly TimeSpan CONNECT_TIMER_INTERVAL = TimeSpan.FromMilliseconds(2000);
        public static readonly TimeSpan CONNECT_TIMER_INIT = TimeSpan.FromMilliseconds(50);
        public static readonly TimeSpan DRIVE_UPDATE_INTERVAL = TimeSpan.FromMilliseconds(2000);
        public static readonly TimeSpan BATTERY_UPDATE_INTERVAL = TimeSpan.FromMilliseconds(8000);

        public static readonly sbyte MIN_SUPPORTED_ANDROID_VER = 6;
        public static readonly double MAX_PANE_HEIGHT_RATIO = 0.4;
        public static readonly int MIN_PANE_HEIGHT = 150;
        public static readonly double MIN_PANE_HEIGHT_RATIO = 0.15;

        public static readonly string[] APK_NAMES = { ".APK", ".XAPK", ".APKS", ".APKM" };

        public static readonly UnicodeCategory[] UNICODE_ICONS = { UnicodeCategory.Surrogate, UnicodeCategory.PrivateUse, UnicodeCategory.OtherSymbol, UnicodeCategory.OtherNotAssigned };

        public static readonly SolidColorBrush QR_BACKGROUND = new(Colors.Transparent);
        public static readonly SolidColorBrush QR_FOREGROUND = new(Color.FromRgb(40, 40, 40));

        public static readonly char[] ALPHABET = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()-+*/<>{}".ToCharArray();
        public static readonly string PAIRING_SERVICE_PREFIX = "adbexplorer-";
        public static readonly string LOOPBACK_IP = "0.0.0.0";

        public static readonly char[] ESCAPE_ADB_SHELL_CHARS = { '(', ')', '<', '>', '|', ';', '&', '*', '\\', '~', '"', '\'', ' ', '$', '`' };
        public static readonly char[] ESCAPE_ADB_CHARS = { '$', '`', '"', '\\' };

        public static readonly Version MIN_ADB_VERSION = new(31, 0, 2);
        public static readonly Version WIN11_VERSION = new(10, 0, 22000);

        public static readonly bool DISPLAY_OFFLINE_SERVICES = false;
    }
}
